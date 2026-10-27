using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MailFolder = Confluir.Core.Models.MailFolder;

namespace Confluir.Mail;

/// <summary>
/// Fonte de dados da interface: lê sempre do cache local (rápido e offline) e mantém o cache em dia
/// em segundo plano. Para cada conta roda um laço de sincronização (fila de operações → pastas → mensagens)
/// e um <see cref="ImapIdleWorker"/> na Caixa de entrada.
/// </summary>
public sealed class SyncingMailboxService : IMailboxService, IHostedService, IAsyncDisposable
{
    /// <summary>Intervalo da sincronização completa (todas as pastas) além dos avisos do IDLE.</summary>
    private static readonly TimeSpan FullSyncInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(500);
    private const int MaxOperationAttempts = 3;

    private readonly IAccountStore _accounts;
    private readonly IMailCache _cache;
    private readonly ImapSyncEngine _engine;
    private readonly ICredentialStore _credentials;
    private readonly ILogger<SyncingMailboxService> _logger;
    private readonly ConcurrentDictionary<Guid, AccountRunner> _runners = new();
    private bool _started;

    public SyncingMailboxService(
        IAccountStore accounts,
        IMailCache cache,
        ImapSyncEngine engine,
        ICredentialStore credentials,
        ILogger<SyncingMailboxService> logger)
    {
        _accounts = accounts;
        _cache = cache;
        _engine = engine;
        _credentials = credentials;
        _logger = logger;
    }

    public event EventHandler<MailboxChangedEventArgs>? MailboxChanged;
    public event EventHandler<AccountStatusEventArgs>? AccountStatusChanged;
    public event EventHandler<OperationFailedEventArgs>? OperationFailed;

    // Leitura (sempre do cache) -------------------------------------------------------------------

    public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken ct = default) =>
        Task.FromResult(_accounts.Accounts);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
        _cache.GetFoldersAsync(accountId, ct);

    public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid folderId, CancellationToken ct = default) =>
        _cache.GetMessagesAsync(folderId, ct);

    public Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(CancellationToken ct = default) =>
        _cache.GetInboxMessagesAsync(ct);

    public async Task<MessageBody> GetMessageBodyAsync(Guid messageId, CancellationToken ct = default)
    {
        if (await _cache.GetBodyAsync(messageId, ct) is { } cached)
            return cached;

        var location = await _cache.GetLocationAsync(messageId, ct)
            ?? throw new InvalidOperationException("A mensagem não está mais no cache.");
        var body = await _engine.FetchBodyAsync(location, ct);
        await _cache.SaveBodyAsync(body, ct);
        return body;
    }

    public SyncStatus GetStatus(Guid accountId) =>
        _runners.TryGetValue(accountId, out var r) ? r.Status : SyncStatus.Unknown;

    // Ações -----------------------------------------------------------------------------------------

    public async Task MarkAsReadAsync(Guid messageId, CancellationToken ct = default)
    {
        var location = await _cache.GetLocationAsync(messageId, ct);
        if (location is null)
            return;
        // Otimista: o cache muda já; o servidor recebe pela fila (e, offline, quando a conexão voltar).
        await _cache.SetReadAsync(messageId, true, ct);
        await _cache.EnqueueAsync(location.AccountId, messageId, OperationKind.MarkRead, ct);
        if (_runners.TryGetValue(location.AccountId, out var runner))
            runner.Wake(queue: true);
    }

    public void RequestSync(Guid? accountId = null)
    {
        foreach (var runner in _runners.Values.Where(r => accountId is null || r.Account.Id == accountId))
            runner.Wake(full: true);
    }

    // Ciclo de vida ---------------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = true;
        _accounts.Changed += OnAccountsChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        foreach (var account in _accounts.Accounts)
            Start(account);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _accounts.Changed -= OnAccountsChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        await Task.WhenAll(_runners.Values.Select(r => r.StopAsync()));
        _runners.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
            return;
        _logger.LogInformation("Rede disponível; sincronizando");
        foreach (var runner in _runners.Values)
            runner.Wake(full: true, queue: true);
    }

    private void OnAccountsChanged(object? sender, EventArgs e) => _ = ReconcileAccountsAsync();

    private async Task ReconcileAccountsAsync()
    {
        var current = _accounts.Accounts.ToDictionary(a => a.Id);
        foreach (var (id, runner) in _runners)
        {
            if (current.TryGetValue(id, out var account) && account == runner.Account)
                continue;
            // Removida ou alterada (servidores, senha…): para e, se removida, limpa o cache da conta.
            _runners.TryRemove(id, out _);
            await runner.StopAsync();
            if (!current.ContainsKey(id))
            {
                await _cache.DeleteAccountAsync(id);
                _logger.LogInformation("Cache da conta {AccountId} apagado", id);
            }
        }
        foreach (var account in current.Values.Where(a => !_runners.ContainsKey(a.Id)))
            Start(account);
    }

    private void Start(MailAccount account)
    {
        if (!_started)
            return;
        var runner = new AccountRunner(account);
        if (!_runners.TryAdd(account.Id, runner))
            return;
        runner.Loop = Task.Run(() => RunAccountAsync(runner));
        var idle = new ImapIdleWorker(account, _credentials, () =>
        {
            runner.Wake(inbox: true);
            return Task.CompletedTask;
        }, _logger);
        runner.Idle = Task.Run(() => idle.RunAsync(runner.Token));
    }

    // Laço por conta --------------------------------------------------------------------------------

    private async Task RunAccountAsync(AccountRunner runner)
    {
        var ct = runner.Token;
        var account = runner.Account;
        var attempt = 0;
        var nextFull = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wait = nextFull - DateTimeOffset.UtcNow;
                if (!runner.HasWork && wait > TimeSpan.Zero)
                    await runner.WaitAsync(wait, ct);

                var (full, inbox, _) = runner.TakeWork();
                full |= DateTimeOffset.UtcNow >= nextFull;

                SetStatus(runner, SyncStatus.Syncing);
                await ProcessQueueAsync(account, ct);

                if (full)
                {
                    var folders = await _engine.SyncFolderListAsync(account, ct);
                    RaiseChanged(account.Id, foldersChanged: true);
                    // Caixa de entrada primeiro: é o que o usuário vê ao abrir o app.
                    foreach (var folder in folders.OrderBy(f => f.Kind == SpecialFolderKind.Inbox ? 0 : 1))
                        await SyncFolderAsync(account, folder, ct);
                    nextFull = DateTimeOffset.UtcNow + FullSyncInterval;
                }
                else if (inbox)
                {
                    var folders = await _cache.GetFoldersAsync(account.Id, ct);
                    if (folders.FirstOrDefault(f => f.Kind == SpecialFolderKind.Inbox) is { } inboxFolder)
                        await SyncFolderAsync(account, inboxFolder, ct);
                }

                attempt = 0;
                SetStatus(runner, SyncStatus.UpToDate);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var error = MailErrorClassifier.Classify(ex, account.Provider).Error;
                var offline = error is ConnectionError.ServerUnreachable or ConnectionError.Timeout
                    or ConnectionError.SecureConnectionFailed;
                SetStatus(runner, offline ? SyncStatus.Offline : SyncStatus.Error, ex);
                _logger.LogWarning(ex, "Sincronização da conta {AccountId} falhou ({Error})", account.Id, error);

                // Erro de credencial não se resolve sozinho: espera mais antes de tentar de novo.
                var delay = offline ? ImapIdleWorker.Backoff(attempt++) : TimeSpan.FromMinutes(5);
                runner.Wake(full: true, signal: false);
                try
                {
                    await runner.WaitAsync(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task SyncFolderAsync(MailAccount account, MailFolder folder, CancellationToken ct)
    {
        var lastRaise = DateTimeOffset.MinValue;
        var changed = await _engine.SyncFolderAsync(account, folder, () =>
        {
            // Lotes do preenchimento inicial: atualiza a interface sem inundá-la de eventos.
            if (DateTimeOffset.UtcNow - lastRaise < ProgressThrottle)
                return;
            lastRaise = DateTimeOffset.UtcNow;
            RaiseChanged(account.Id, foldersChanged: false);
        }, ct);
        if (changed)
            RaiseChanged(account.Id, foldersChanged: false);
    }

    private async Task ProcessQueueAsync(MailAccount account, CancellationToken ct)
    {
        foreach (var op in await _cache.GetPendingAsync(account.Id, ct))
        {
            var location = await _cache.GetLocationAsync(op.MessageId, ct);
            if (location is null)
            {
                await _cache.CompleteAsync(op.Id, ct); // a mensagem saiu do servidor
                continue;
            }

            try
            {
                await _engine.ApplyAsync(op.Kind, location, ct);
                await _cache.CompleteAsync(op.Id, ct);
            }
            catch (Exception ex) when (IsPermanent(ex, account))
            {
                await _cache.RecordFailureAsync(op.Id, ct);
                if (op.Attempts + 1 < MaxOperationAttempts)
                    continue;

                // Recusada de vez: desfaz no cache e avisa a interface.
                _logger.LogWarning(ex, "Operação {Kind} recusada pelo servidor; desfazendo", op.Kind);
                await _cache.CompleteAsync(op.Id, ct);
                await _cache.SetReadAsync(op.MessageId, op.Kind != OperationKind.MarkRead, ct);
                OperationFailed?.Invoke(this, new OperationFailedEventArgs(op.MessageId, op.Kind));
                RaiseChanged(account.Id, foldersChanged: false);
            }
        }
    }

    /// <summary>Falhas de rede ficam na fila para depois; as demais contam como tentativa.</summary>
    private static bool IsPermanent(Exception ex, MailAccount account) =>
        ex is not OperationCanceledException &&
        MailErrorClassifier.Classify(ex, account.Provider).Error is not
            (ConnectionError.ServerUnreachable or ConnectionError.Timeout or ConnectionError.SecureConnectionFailed);

    private void SetStatus(AccountRunner runner, SyncStatus status, Exception? error = null)
    {
        if (runner.Status == status && error is null)
            return;
        runner.Status = status;
        AccountStatusChanged?.Invoke(this, new AccountStatusEventArgs(runner.Account.Id, status, error));
    }

    private void RaiseChanged(Guid accountId, bool foldersChanged) =>
        MailboxChanged?.Invoke(this, new MailboxChangedEventArgs(accountId, foldersChanged));

    /// <summary>Estado de uma conta em sincronização: pedidos pendentes e as tarefas em execução.</summary>
    private sealed class AccountRunner(MailAccount account)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _signal = new(0);
        private volatile bool _full = true;
        private volatile bool _inbox;
        private volatile bool _queue = true;

        public MailAccount Account { get; } = account;
        public CancellationToken Token => _cts.Token;
        public SyncStatus Status { get; set; } = SyncStatus.Unknown;
        public Task? Loop { get; set; }
        public Task? Idle { get; set; }

        public bool HasWork => _full || _inbox || _queue;

        public void Wake(bool full = false, bool inbox = false, bool queue = false, bool signal = true)
        {
            _full |= full;
            _inbox |= inbox;
            _queue |= queue;
            if (signal)
                _signal.Release();
        }

        public (bool Full, bool Inbox, bool Queue) TakeWork()
        {
            var work = (_full, _inbox, _queue);
            _full = _inbox = _queue = false;
            while (_signal.CurrentCount > 0)
                _signal.Wait(0);
            return work;
        }

        public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

        public async Task StopAsync()
        {
            await _cts.CancelAsync();
            foreach (var task in new[] { Loop, Idle })
            {
                if (task is null)
                    continue;
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // Cancelamento/tempo esgotado ao encerrar.
                }
            }
        }
    }
}

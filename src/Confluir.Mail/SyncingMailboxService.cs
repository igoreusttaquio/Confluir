using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Confluir.Core.Models;
using Confluir.Core.Rules;
using Confluir.Core.Search;
using Confluir.Core.Services;
using Confluir.Core.Settings;
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
    private readonly MailAuthenticator _authenticator;
    private readonly ISettingsStore _settings;
    private readonly ILogger<SyncingMailboxService> _logger;
    private readonly ConcurrentDictionary<Guid, AccountRunner> _runners = new();
    private bool _started;

    public SyncingMailboxService(
        IAccountStore accounts,
        IMailCache cache,
        ImapSyncEngine engine,
        MailAuthenticator authenticator,
        ISettingsStore settings,
        ILogger<SyncingMailboxService> logger)
    {
        _settings = settings;
        _accounts = accounts;
        _cache = cache;
        _engine = engine;
        _authenticator = authenticator;
        _logger = logger;
    }

    public event EventHandler<MailboxChangedEventArgs>? MailboxChanged;
    public event EventHandler<AccountStatusEventArgs>? AccountStatusChanged;
    public event EventHandler<OperationFailedEventArgs>? OperationFailed;
    public event EventHandler<MessagesArrivedEventArgs>? MessagesArrived;

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

    public async Task<byte[]> GetAttachmentAsync(Guid messageId, int attachmentIndex, CancellationToken ct = default)
    {
        var location = await _cache.GetLocationAsync(messageId, ct)
            ?? throw new InvalidOperationException("A mensagem não está mais no cache.");
        return await _engine.FetchAttachmentAsync(location, attachmentIndex, ct);
    }

    public Task<IReadOnlyList<MailAddress>> SearchAddressesAsync(string text, int limit = 8, CancellationToken ct = default) =>
        _cache.SearchAddressesAsync(text, limit, ct);

    public SyncStatus GetStatus(Guid accountId) =>
        _runners.TryGetValue(accountId, out var r) ? r.Status : SyncStatus.Unknown;

    // Ações -----------------------------------------------------------------------------------------

    // Otimista: o cache muda já; o servidor recebe pela fila (e, offline, quando a conexão voltar).

    public Task SetReadAsync(Guid messageId, bool isRead, CancellationToken ct = default) =>
        EnqueueAsync(messageId, isRead ? OperationKind.MarkRead : OperationKind.MarkUnread,
            () => _cache.SetReadAsync(messageId, isRead, ct), keepLocation: false, ct);

    public Task SetFlaggedAsync(Guid messageId, bool isFlagged, CancellationToken ct = default) =>
        EnqueueAsync(messageId, isFlagged ? OperationKind.Flag : OperationKind.Unflag,
            () => _cache.SetFlaggedAsync(messageId, isFlagged, ct), keepLocation: false, ct);

    public Task ArchiveAsync(Guid messageId, CancellationToken ct = default) =>
        EnqueueAsync(messageId, OperationKind.Archive, () => _cache.DeleteMessageAsync(messageId, ct), keepLocation: true, ct);

public Task DeleteAsync(Guid messageId, CancellationToken ct = default) =>
        EnqueueAsync(messageId, OperationKind.Delete, () => _cache.DeleteMessageAsync(messageId, ct), keepLocation: true, ct);

    public async Task MoveAsync(Guid messageId, string folderFullName, CancellationToken ct = default)
    {
        var location = await _cache.GetLocationAsync(messageId, ct);
        if (location is null || location.FolderFullName == folderFullName)
            return;
        await _cache.DeleteMessageAsync(messageId, ct);
        await _cache.EnqueueAsync(location.AccountId, messageId, OperationKind.Move, location, folderFullName, ct);
        Wake(location.AccountId);
    }

    // Busca -----------------------------------------------------------------------------------------

    public Task<IReadOnlyList<MessageSummary>> SearchAsync(MessageSearch search, int limit = 500, CancellationToken ct = default) =>
        _cache.SearchMessagesAsync(search, limit, ct);

    public async Task<int> SearchServerAsync(Guid folderId, SearchQuery query, CancellationToken ct = default)
    {
        foreach (var account in _accounts.Accounts)
        {
            var folder = (await _cache.GetFoldersAsync(account.Id, ct)).FirstOrDefault(f => f.Id == folderId);
            if (folder is null)
                continue;
            var count = await _engine.SearchServerAsync(account, folder, query, ct);
            if (count > 0)
                RaiseChanged(account.Id, foldersChanged: false);
            return count;
        }
        return 0;
    }

    // Regras ----------------------------------------------------------------------------------------

    /// <summary>"Aplicar agora": roda a regra nas mensagens que já estão na caixa de entrada.</summary>
    public async Task<int> ApplyRuleAsync(MailRule rule, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var account in _accounts.Accounts.Where(a => rule.AccountId is null || rule.AccountId == a.Id))
        {
            var inbox = (await _cache.GetFoldersAsync(account.Id, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Inbox);
            if (inbox is null)
                continue;
            var messages = await _cache.GetMessagesAsync(inbox.Id, ct);
            total += await ExecuteRulesAsync(account, [rule with { Enabled = true }], messages, ct);
        }
        return total;
    }

    /// <summary>
    /// Executa o resultado das regras. Cada ação vai para a fila com o local da mensagem guardado,
    /// porque mover/arquivar tira a mensagem do cache antes de a fila marcar como lida ou sinalizar.
    /// </summary>
    private async Task<int> ExecuteRulesAsync(MailAccount account, IReadOnlyList<MailRule> rules,
        IReadOnlyList<MessageSummary> messages, CancellationToken ct) =>
        (await ExecuteRulesWithOutcomesAsync(account, rules, messages, ct)).Count;

    private async Task<IReadOnlyList<RuleOutcome>> ExecuteRulesWithOutcomesAsync(MailAccount account, IReadOnlyList<MailRule> rules,
        IReadOnlyList<MessageSummary> messages, CancellationToken ct)
    {
        var outcomes = RuleEngine.Evaluate(rules, account.Id, messages);
        foreach (var outcome in outcomes)
        {
            var id = outcome.Message.Id;
            var location = await _cache.GetLocationAsync(id, ct);
            if (location is null)
                continue;
            var actions = outcome.Actions;
            if (actions.MarkRead)
            {
                await _cache.SetReadAsync(id, true, ct);
                await _cache.EnqueueAsync(account.Id, id, OperationKind.MarkRead, location, ct: ct);
            }
            if (actions.Flag)
            {
                await _cache.SetFlaggedAsync(id, true, ct);
                await _cache.EnqueueAsync(account.Id, id, OperationKind.Flag, location, ct: ct);
            }
            OperationKind? kind = actions.Destination switch
            {
                RuleDestination.Archive => OperationKind.Archive,
                RuleDestination.Delete => OperationKind.Delete,
                RuleDestination.MoveToFolder => OperationKind.Move,
                _ => null,
            };
            var target = kind == OperationKind.Move ? actions.MoveToFolder : null;
            if (kind is { } destination && (destination != OperationKind.Move || target != location.FolderFullName))
            {
                await _cache.DeleteMessageAsync(id, ct);
                await _cache.EnqueueAsync(account.Id, id, destination, location, target, ct);
            }
        }
        if (outcomes.Count > 0)
        {
            _logger.LogInformation("Regras aplicadas a {Count} mensagens (conta {AccountId})", outcomes.Count, account.Id);
            Wake(account.Id);
            RaiseChanged(account.Id, foldersChanged: false);
        }
        return outcomes;
    }

    private void Wake(Guid accountId)
    {
        if (_runners.TryGetValue(accountId, out var runner))
            runner.Wake(queue: true);
    }

    /// <param name="keepLocation">A ação tira a mensagem do cache: a fila guarda onde ela estava no servidor.</param>
    private async Task EnqueueAsync(Guid messageId, OperationKind kind, Func<Task> applyLocally, bool keepLocation,
        CancellationToken ct)
    {
        var location = await _cache.GetLocationAsync(messageId, ct);
        if (location is null)
            return;
        await applyLocally();
        await _cache.EnqueueAsync(location.AccountId, messageId, kind, keepLocation ? location : null, ct: ct);
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
        var idle = new ImapIdleWorker(account, _authenticator, () =>
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
        var arrived = folder.Kind == SpecialFolderKind.Inbox ? new List<MessageSummary>() : null;
        var changed = await _engine.SyncFolderAsync(account, folder, () =>
        {
            // Lotes do preenchimento inicial: atualiza a interface sem inundá-la de eventos.
            if (DateTimeOffset.UtcNow - lastRaise < ProgressThrottle)
                return;
            lastRaise = DateTimeOffset.UtcNow;
            RaiseChanged(account.Id, foldersChanged: false);
        }, ct, arrived);
        if (changed)
            RaiseChanged(account.Id, foldersChanged: false);

        if (arrived is not { Count: > 0 })
            return;

        // Regras só nas que acabaram de chegar; as ações vão para a fila e são enviadas na próxima volta do laço.
        IReadOnlyList<RuleOutcome> outcomes = [];
        if (_settings.Current.Rules is { Count: > 0 } rules)
        {
            try
            {
                outcomes = await ExecuteRulesWithOutcomesAsync(account, rules.ToList(), arrived, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Falha ao aplicar regras (conta {AccountId})", account.Id);
            }
        }

        // Notifica as que continuam não lidas na caixa de entrada.
        var handled = outcomes.Where(o => o.Actions.MarkRead || o.Actions.Destination != RuleDestination.None)
            .Select(o => o.Message.Id).ToHashSet();
        var notify = arrived.Where(m => !m.IsRead && !handled.Contains(m.Id)).ToList();
        if (notify.Count > 0)
            MessagesArrived?.Invoke(this, new MessagesArrivedEventArgs(account.Id, notify));
    }

    private async Task ProcessQueueAsync(MailAccount account, CancellationToken ct)
    {
        var pending = await _cache.GetPendingAsync(account.Id, ct);
        if (pending.Count == 0)
            return;
        var folders = await _cache.GetFoldersAsync(account.Id, ct);

        foreach (var op in pending)
        {
            // Mover/excluir já tiraram a mensagem do cache: usam o local guardado na fila.
            var location = op.Location ?? await _cache.GetLocationAsync(op.MessageId, ct);
            if (location is null)
            {
                await _cache.CompleteAsync(op.Id, ct); // a mensagem saiu do servidor
                continue;
            }

            try
            {
                await _engine.ApplyAsync(op.Kind, location, folders, ct, op.TargetFolder);
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
                await RevertAsync(op, ct);
                OperationFailed?.Invoke(this, new OperationFailedEventArgs(op.MessageId, op.Kind));
                RaiseChanged(account.Id, foldersChanged: false);
            }
        }
    }

    private async Task RevertAsync(PendingOperation op, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case OperationKind.MarkRead or OperationKind.MarkUnread:
                await _cache.SetReadAsync(op.MessageId, op.Kind == OperationKind.MarkUnread, ct);
                break;
            case OperationKind.Flag or OperationKind.Unflag:
                await _cache.SetFlaggedAsync(op.MessageId, op.Kind == OperationKind.Unflag, ct);
                break;
            default:
                // Arquivar/excluir: a mensagem continua no servidor; a próxima sincronização a traz de volta.
                if (_runners.TryGetValue(op.AccountId, out var runner))
                    runner.Wake(full: true, signal: false);
                break;
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

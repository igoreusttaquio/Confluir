using System.Collections.Concurrent;
using System.Net.Sockets;
using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace Confluir.Mail;

/// <summary>
/// Uma conexão IMAP autenticada por conta, usada por uma operação de cada vez.
/// Reconecta automaticamente se a conexão tiver caído. (A fila de operações e o IDLE chegam na fase 3.)
/// </summary>
public sealed class ImapConnectionPool : IAsyncDisposable, IDisposable
{
    private const int TimeoutMs = 30_000;

    private readonly IAccountStore _accounts;
    private readonly MailAuthenticator _authenticator;
    private readonly ILogger<ImapConnectionPool> _logger;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public ImapConnectionPool(IAccountStore accounts, MailAuthenticator authenticator, ILogger<ImapConnectionPool> logger)
    {
        _accounts = accounts;
        _authenticator = authenticator;
        _logger = logger;
        _accounts.Changed += (_, _) => _ = DropStaleConnectionsAsync();
    }

    public async Task<T> RunAsync<T>(Guid accountId, Func<ImapClient, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var account = _accounts.Accounts.FirstOrDefault(a => a.Id == accountId)
            ?? throw new InvalidOperationException($"Conta {accountId} não encontrada.");
        var connection = _connections.GetOrAdd(accountId, _ => new Connection(account));

        await connection.Gate.WaitAsync(ct);
        try
        {
            await EnsureReadyAsync(connection, ct);
            try
            {
                return await operation(connection.Client, ct);
            }
            catch (Exception ex) when (IsConnectionLost(ex) && !ct.IsCancellationRequested)
            {
                // Conexão ociosa derrubada pelo servidor: reconecta uma vez e repete (operações são idempotentes).
                _logger.LogInformation("Conexão IMAP da conta {AccountId} caiu ({Error}); reconectando", accountId, ex.GetType().Name);
                connection.Reset();
                await EnsureReadyAsync(connection, ct);
                return await operation(connection.Client, ct);
            }
        }
        catch
        {
            // Após erro de protocolo o estado da conexão é incerto: descarta para a próxima operação.
            if (!connection.Client.IsConnected)
                connection.Reset();
            throw;
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
            await connection.CloseAsync();
        _connections.Clear();
    }

    // O host é descartado de forma síncrona ao fechar o app.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task EnsureReadyAsync(Connection connection, CancellationToken ct)
    {
        var client = connection.Client;
        var account = connection.Account;
        if (!client.IsConnected)
        {
            client.Timeout = TimeoutMs;
            await client.ConnectAsync(account.Servers.Imap.Host, account.Servers.Imap.Port,
                account.Servers.Imap.Security.ToSocketOptions(), ct);
        }
        if (!client.IsAuthenticated)
        {
            await _authenticator.AuthenticateAsync(client, account, ct);
        }
    }

    private static bool IsConnectionLost(Exception ex) =>
        ex is IOException or SocketException or ImapProtocolException or ServiceNotConnectedException;

    private async Task DropStaleConnectionsAsync()
    {
        var current = _accounts.Accounts.ToDictionary(a => a.Id);
        foreach (var (id, connection) in _connections)
        {
            if (current.TryGetValue(id, out var account) && account == connection.Account)
                continue;
            if (_connections.TryRemove(id, out _))
                await connection.CloseAsync();
        }
    }

    private sealed class Connection(MailAccount account)
    {
        public MailAccount Account { get; } = account;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ImapClient Client { get; private set; } = new();

        public void Reset()
        {
            Client.Dispose();
            Client = new ImapClient();
        }

        public async Task CloseAsync()
        {
            try
            {
                if (Client.IsConnected)
                    await Client.DisconnectAsync(quit: true);
            }
            catch (Exception)
            {
                // Encerrando: falhas ao desconectar não importam.
            }
            Client.Dispose();
        }
    }
}

using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace Confluir.Mail;

/// <summary>
/// Mantém uma conexão dedicada em IMAP IDLE na Caixa de entrada e avisa quando algo muda
/// (mensagem nova, apagada ou flags). Reconecta sozinho com espera exponencial.
/// Sem suporte a IDLE no servidor, consulta periodicamente.
/// </summary>
public sealed class ImapIdleWorker(
    MailAccount account,
    MailAuthenticator authenticator,
    Func<Task> onInboxChanged,
    ILogger logger)
{
    // RFC 2177: renovar o IDLE antes de 29 minutos; muitos servidores e NATs cortam antes disso.
    private static readonly TimeSpan IdleRenewal = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectedAsync(() => attempt = 0, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = Backoff(attempt++);
                logger.LogInformation("IDLE da conta {AccountId} caiu ({Error}); nova tentativa em {Delay}",
                    account.Id, ex.GetType().Name, delay);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>1s, 2s, 4s… até 5 minutos.</summary>
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(attempt, 16)), MaxBackoff.TotalSeconds));

    private async Task RunConnectedAsync(Action onConnected, CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = 60_000 };
        await client.ConnectAsync(account.Servers.Imap.Host, account.Servers.Imap.Port,
            account.Servers.Imap.Security.ToSocketOptions(), ct);
        await authenticator.AuthenticateAsync(client, account, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        onConnected();

        var changed = false;
        CancellationTokenSource? idleDone = null;
        void Signal(object? sender, EventArgs e)
        {
            changed = true;
            idleDone?.Cancel(); // sai do IDLE para sincronizar
        }
        inbox.CountChanged += Signal;
        inbox.MessageExpunged += Signal;
        inbox.MessageFlagsChanged += Signal;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    using var done = new CancellationTokenSource(IdleRenewal);
                    idleDone = done;
                    await client.IdleAsync(done.Token, ct);
                    idleDone = null;
                }
                else
                {
                    await Task.Delay(PollInterval, ct);
                    await client.NoOpAsync(ct);
                }

                if (changed)
                {
                    changed = false;
                    await onInboxChanged();
                }
            }
        }
        finally
        {
            inbox.CountChanged -= Signal;
            inbox.MessageExpunged -= Signal;
            inbox.MessageFlagsChanged -= Signal;
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(quit: true, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Encerrando.
                }
            }
        }
    }
}

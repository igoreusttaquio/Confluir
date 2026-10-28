using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Confluir.Mail;

/// <summary>
/// Envia pelo SMTP da conta. Gmail, Outlook.com e Yahoo guardam a cópia em Enviados sozinhos;
/// para os demais servidores IMAP a cópia é adicionada (APPEND) à pasta Enviados.
/// </summary>
public sealed class SmtpMailSender(
    IAccountStore accounts,
    IMailCache cache,
    MailAuthenticator authenticator,
    ImapConnectionPool pool,
    ILogger<SmtpMailSender> logger) : IMailSender
{
    private const int TimeoutMs = 60_000;

    public async Task SendAsync(OutgoingMessage message, Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment,
        CancellationToken ct = default)
    {
        var account = accounts.Accounts.FirstOrDefault(a => a.Id == message.AccountId)
            ?? throw new InvalidOperationException("Conta de envio não encontrada.");
        var mime = await BuildAsync(message, account, loadAttachment, ct);

        using (var smtp = new SmtpClient { Timeout = TimeoutMs })
        {
            var endpoint = account.Servers.Smtp;
            await smtp.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security.ToSocketOptions(), ct);
            await authenticator.AuthenticateAsync(smtp, account, ct);
            await smtp.SendAsync(mime, ct);
            await smtp.DisconnectAsync(quit: true, ct);
        }
        logger.LogInformation("Mensagem enviada pela conta {AccountId}", account.Id);

        if (account.Provider is MailProvider.Imap)
            await SaveToSentAsync(account, mime, ct);
    }

    public static async Task<MimeMessage> BuildAsync(OutgoingMessage message, MailAccount account,
        Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(account.SenderName ?? "", account.Email));
        mime.To.AddRange(message.To.Select(ToMailbox));
        mime.Cc.AddRange(message.Cc.Select(ToMailbox));
        mime.Bcc.AddRange(message.Bcc.Select(ToMailbox));
        mime.Subject = message.Subject;
        if (message.InReplyTo is { } inReplyTo)
            mime.InReplyTo = inReplyTo.Trim('<', '>');
        foreach (var reference in message.References ?? [])
            mime.References.Add(reference.Trim('<', '>'));

        var builder = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var attachment in message.Attachments)
        {
            var data = attachment.IsFromMessage
                ? await loadAttachment(attachment, ct)
                : await File.ReadAllBytesAsync(attachment.FilePath!, ct);
            builder.Attachments.Add(attachment.FileName, data, ContentType.Parse(MimeTypes.GetMimeType(attachment.FileName)));
        }
        mime.Body = builder.ToMessageBody();
        return mime;
    }

    private async Task SaveToSentAsync(MailAccount account, MimeMessage mime, CancellationToken ct)
    {
        try
        {
            var sent = (await cache.GetFoldersAsync(account.Id, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Sent);
            if (sent is null)
                return;
            // Cópia sem Bcc visível para outros, como os clientes de email fazem.
            await pool.RunAsync(account.Id, async (client, token) =>
            {
                var folder = await client.GetFolderAsync(sent.FullName, token);
                await folder.AppendAsync(new AppendRequest(mime, MessageFlags.Seen), token);
                return true;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A mensagem já foi enviada; só a cópia em Enviados falhou.
            logger.LogWarning(ex, "Mensagem enviada, mas a cópia em Enviados falhou (conta {AccountId})", account.Id);
        }
    }

    private static MailboxAddress ToMailbox(Core.Models.MailAddress a) => new(a.Name ?? "", a.Address);
}

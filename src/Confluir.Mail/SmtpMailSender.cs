using System.Text.RegularExpressions;
using Confluir.Core.Compose;
using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Confluir.Mail;

/// <summary>
/// Envia pelo SMTP da conta. Gmail, Outlook.com e Yahoo guardam a cópia em Enviados sozinhos;
/// para os demais servidores IMAP a cópia é adicionada (APPEND) à pasta Enviados.
/// </summary>
public sealed partial class SmtpMailSender(
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

        var builder = new BodyBuilder { TextBody = message.TextBody };
        if (message.HtmlBody is { } html)
            builder.HtmlBody = EmbedDataImages(html, builder);
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

    /// <summary>
    /// Imagens coladas no editor chegam como <c>data:</c>, que vários clientes (ex.: Gmail) não exibem:
    /// viram partes embutidas referenciadas por <c>cid:</c>.
    /// </summary>
    public static string EmbedDataImages(string html, BodyBuilder builder) =>
        DataImage().Replace(html, m =>
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(m.Groups["data"].Value);
            }
            catch (FormatException)
            {
                return m.Value;
            }
            var type = ContentType.Parse(m.Groups["type"].Value);
            var name = $"imagem{builder.LinkedResources.Count + 1}.{type.MediaSubtype.ToLowerInvariant()}";
            var part = builder.LinkedResources.Add(name, data, type);
            part.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId();
            return $"{m.Groups["prefix"].Value}cid:{part.ContentId}";
        });

    [GeneratedRegex(@"(?<prefix>\bsrc\s*=\s*[""']?)data:(?<type>image/[a-z0-9.+-]+);base64,(?<data>[a-z0-9+/=\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DataImage();

    public async Task<ServerDraft?> SaveDraftAsync(Guid draftId, OutgoingMessage message, ServerDraft? previous,
        Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment, CancellationToken ct = default)
    {
        var account = accounts.Accounts.FirstOrDefault(a => a.Id == message.AccountId)
            ?? throw new InvalidOperationException("Conta do rascunho não encontrada.");
        var drafts = (await cache.GetFoldersAsync(account.Id, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Drafts);
        if (drafts is null)
            return null;

        var mime = await BuildAsync(message, account, loadAttachment, ct);
        mime.MessageId = DraftMessageId.For(draftId);

        var saved = await pool.RunAsync(account.Id, async (client, token) =>
        {
            var folder = await client.GetFolderAsync(drafts.FullName, token);
            var uid = await folder.AppendAsync(new AppendRequest(mime, MessageFlags.Draft | MessageFlags.Seen), token);
            if (uid is null)
            {
                // Sem UIDPLUS: procura a cópia recém-gravada pelo Message-Id (a mais nova que não seja a anterior).
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
                var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", mime.MessageId), token);
                uid = found.Where(u => previous is null || previous.FolderFullName != drafts.FullName || u.Id != previous.Uid)
                    .Select(u => (UniqueId?)u).LastOrDefault();
            }
            return uid is { } u ? new ServerDraft(drafts.FullName, u.Id) : null;
        }, ct);

        if (previous is not null && previous != saved)
            await DeleteDraftAsync(account.Id, previous, ct);
        logger.LogInformation("Rascunho gravado no servidor (conta {AccountId})", account.Id);
        return saved;
    }

    public Task DeleteDraftAsync(Guid accountId, ServerDraft draft, CancellationToken ct = default) =>
        pool.RunAsync(accountId, async (client, token) =>
        {
            var folder = await client.GetFolderAsync(draft.FolderFullName, token);
            await folder.OpenAsync(FolderAccess.ReadWrite, token);
            var uid = new UniqueId(draft.Uid);
            await folder.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, token);
            if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                await folder.ExpungeAsync([uid], token);
            else
                await folder.ExpungeAsync(token);
            return true;
        }, ct);

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

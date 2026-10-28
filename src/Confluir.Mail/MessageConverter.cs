using System.Net;
using System.Text.RegularExpressions;
using Confluir.Core.Models;
using MailKit;
using MimeKit;
using MailAddress = Confluir.Core.Models.MailAddress;
using MessageSummary = Confluir.Core.Models.MessageSummary;

namespace Confluir.Mail;

/// <summary>Converte estruturas do MailKit/MimeKit para os modelos do Core.</summary>
public static partial class MessageConverter
{
    private const int PreviewLength = 200;

    public static MessageSummary ToSummary(IMessageSummary m, Guid id, Guid accountId, Guid folderId)
    {
        var from = m.Envelope?.From.Mailboxes.FirstOrDefault();
        return new MessageSummary(
            id,
            accountId,
            folderId,
            FromName: from?.Name ?? "",
            FromAddress: from?.Address ?? "",
            Subject: m.Envelope?.Subject ?? m.NormalizedSubject ?? "",
            Preview: Collapse(m.PreviewText ?? "", PreviewLength),
            Date: BestDate(m.Envelope?.Date, m.InternalDate),
            IsRead: m.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            IsFlagged: m.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
            HasAttachments: m.Body is not null && m.Attachments.Any(),
            Recipients: RecipientsOf(m.Envelope));
    }

    /// <summary>Endereços de Para e Cc em minúsculas, separados por espaço.</summary>
    public static string? RecipientsOf(Envelope? envelope)
    {
        if (envelope is null)
            return null;
        var all = envelope.To.Mailboxes.Concat(envelope.Cc.Mailboxes)
            .Select(a => a.Address?.Trim().ToLowerInvariant())
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct();
        var text = string.Join(' ', all);
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Data do cabeçalho <c>Date</c>; se faltar ou for absurda (antes de 1970 ou mais de um dia no futuro),
    /// usa a data de recebimento no servidor (INTERNALDATE); sem nenhuma, 1/1/1970.
    /// </summary>
    public static DateTimeOffset BestDate(DateTimeOffset? header, DateTimeOffset? internalDate)
    {
        var limit = DateTimeOffset.UtcNow.AddDays(1);
        static bool Plausible(DateTimeOffset d, DateTimeOffset max) => d >= DateTimeOffset.UnixEpoch && d <= max;

        if (header is { } h && Plausible(h, limit))
            return h;
        if (internalDate is { } i && i >= DateTimeOffset.UnixEpoch)
            return i;
        return DateTimeOffset.UnixEpoch;
    }

    public static MessageBody ToBody(MimeMessage message, Guid id)
    {
        var html = message.HtmlBody;
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && html is not null)
            text = HtmlToText(html);

        var attachments = AttachmentParts(message)
            .Select((p, i) => new AttachmentInfo(
                p.FileName ?? p.ContentType.Name ?? "anexo",
                p.ContentType.MimeType.ToLowerInvariant(),
                EstimateSize(p),
                i))
            .ToList();

        return new MessageBody(
            id,
            ToAddresses(message.To),
            ToAddresses(message.Cc),
            text?.Trim() ?? "",
            attachments,
            html is null ? null : InlineCidImages(html, message),
            MessageIdHeader: string.IsNullOrWhiteSpace(message.MessageId) ? null : $"<{message.MessageId}>",
            References: message.References.Select(r => $"<{r}>").ToList(),
            ReplyTo: ToAddresses(message.ReplyTo));
    }

    /// <summary>Anexos na mesma ordem usada por <see cref="AttachmentInfo.Index"/>.</summary>
    public static IEnumerable<MimePart> AttachmentParts(MimeMessage message) => message.Attachments.OfType<MimePart>();

    /// <summary>Conteúdo decodificado do anexo de índice <paramref name="index"/>.</summary>
    public static byte[] ReadAttachment(MimeMessage message, int index, CancellationToken ct = default)
    {
        var part = AttachmentParts(message).ElementAtOrDefault(index);
        if (part?.Content is null)
            throw new InvalidOperationException($"Anexo {index} não encontrado na mensagem.");
        using var output = new MemoryStream();
        part.Content.DecodeTo(output, ct);
        return output.ToArray();
    }

    // Imagens embutidas acima deste tamanho não são convertidas (o HTML ficaria enorme no cache).
    private const long MaxInlineImageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Troca <c>src="cid:…"</c> pelas imagens embutidas na própria mensagem, como <c>data:</c>.
    /// Assim o HTML pode ser exibido sem acesso à rede.
    /// </summary>
    public static string InlineCidImages(string html, MimeMessage message)
    {
        if (!html.Contains("cid:", StringComparison.OrdinalIgnoreCase))
            return html;

        var parts = message.BodyParts
            .OfType<MimePart>()
            .Where(p => !string.IsNullOrEmpty(p.ContentId))
            .GroupBy(p => p.ContentId!.Trim('<', '>'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return CidReference().Replace(html, m =>
        {
            var cid = Uri.UnescapeDataString(m.Groups[2].Value);
            if (!parts.TryGetValue(cid, out var part) || part.Content is null)
                return m.Value;
            using var data = new MemoryStream();
            part.Content.DecodeTo(data);
            if (data.Length > MaxInlineImageBytes)
                return m.Value;
            return $"{m.Groups[1].Value}data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(data.ToArray())}";
        });
    }

    [GeneratedRegex(@"(\bsrc\s*=\s*[""']?)cid:([^""'\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidReference();

    /// <summary>
    /// Texto legível a partir de HTML, para o modo texto puro até o visualizador HTML (fase 4).
    /// Remove estilos/scripts, converte quebras de bloco e decodifica entidades.
    /// </summary>
    public static string HtmlToText(string html)
    {
        var s = InvisibleBlocks().Replace(html, "");
        s = LineBreaks().Replace(s, "\n");
        s = BlockEnds().Replace(s, "\n");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = HorizontalSpace().Replace(s, " ");
        s = SpaceAroundNewlines().Replace(s, "\n");
        s = ExtraBlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    private static List<MailAddress> ToAddresses(InternetAddressList list) =>
        list.Mailboxes.Select(m => new MailAddress(m.Name ?? "", m.Address)).ToList();

    private static long EstimateSize(MimePart part)
    {
        if (part.ContentDisposition?.Size is { } declared)
            return declared;
        if (part.Content is { Stream.CanSeek: true } content)
        {
            // O stream guarda o conteúdo na codificação de Content.Encoding; base64 ocupa ~4/3 do tamanho real.
            var length = content.Stream.Length;
            return content.Encoding == ContentEncoding.Base64 ? length * 3 / 4 : length;
        }
        return 0;
    }

    private static string Collapse(string text, int max)
    {
        var s = AnyWhitespace().Replace(text, " ").Trim();
        return s.Length <= max ? s : s[..max];
    }

    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InvisibleBlocks();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"</(p|div|tr|li|h[1-6]|blockquote|table)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpaceAroundNewlines();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"\s+")]
    private static partial Regex AnyWhitespace();
}

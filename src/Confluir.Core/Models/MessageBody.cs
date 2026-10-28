namespace Confluir.Core.Models;

public sealed record MailAddress(string Name, string Address)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

/// <param name="Index">Posição entre os anexos da mensagem, usada para baixá-lo sob demanda.</param>
public sealed record AttachmentInfo(string FileName, string MimeType, long Size, int Index = 0);

/// <summary>Conteúdo completo de uma mensagem, carregado sob demanda.</summary>
/// <param name="HtmlBody">
/// HTML original (ainda não sanitizado), com as imagens embutidas (<c>cid:</c>) já convertidas em <c>data:</c>.
/// Nulo para mensagens só de texto. A sanitização acontece na hora de exibir.
/// </param>
public sealed record MessageBody(
    Guid MessageId,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    string TextBody,
    IReadOnlyList<AttachmentInfo> Attachments,
    string? HtmlBody = null,
    string? MessageIdHeader = null,
    IReadOnlyList<string>? References = null,
    IReadOnlyList<MailAddress>? ReplyTo = null);

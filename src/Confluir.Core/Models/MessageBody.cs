namespace Confluir.Core.Models;

public sealed record MailAddress(string Name, string Address)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public sealed record AttachmentInfo(string FileName, string MimeType, long Size);

/// <summary>Conteúdo completo de uma mensagem, carregado sob demanda.</summary>
public sealed record MessageBody(
    Guid MessageId,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    string TextBody,
    IReadOnlyList<AttachmentInfo> Attachments);

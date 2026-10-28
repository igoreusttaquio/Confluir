namespace Confluir.Core.Models;

/// <summary>Anexo de uma mensagem a enviar: arquivo do disco ou anexo de outra mensagem (ao encaminhar).</summary>
public sealed record OutgoingAttachment(string FileName, long Size, string? FilePath = null,
    Guid? SourceMessageId = null, int SourceIndex = 0)
{
    public bool IsFromMessage => SourceMessageId is not null;
}

/// <param name="InReplyTo">Message-Id da mensagem respondida (mantém a conversa agrupada nos clientes).</param>
/// <param name="References">Cadeia de Message-Ids da conversa.</param>
public sealed record OutgoingMessage(
    Guid AccountId,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> Bcc,
    string Subject,
    string TextBody,
    string? HtmlBody,
    IReadOnlyList<OutgoingAttachment> Attachments,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null);

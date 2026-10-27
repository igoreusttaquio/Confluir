namespace Confluir.Core.Models;

/// <summary>Cabeçalho/envelope de uma mensagem, suficiente para a lista.</summary>
public sealed record MessageSummary(
    Guid Id,
    Guid AccountId,
    Guid FolderId,
    string FromName,
    string FromAddress,
    string Subject,
    string Preview,
    DateTimeOffset Date,
    bool IsRead,
    bool IsFlagged,
    bool HasAttachments);

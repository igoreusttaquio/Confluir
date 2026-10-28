using Confluir.Core.Models;
using Confluir.Core.Search;

namespace Confluir.Core.Services;

/// <summary>Estado de sincronização de uma pasta no cache local.</summary>
/// <param name="HighestModSeq">MODSEQ mais alto visto (CONDSTORE); 0 se o servidor não suporta.</param>
public sealed record FolderSyncState(uint UidValidity, ulong HighestModSeq, DateTimeOffset LastSyncedAt);

/// <summary>Mensagem como sincronizada do servidor: o resumo e o UID IMAP.</summary>
public sealed record CachedMessage(MessageSummary Summary, uint Uid);

/// <summary>Onde uma mensagem está no servidor.</summary>
public sealed record MessageLocation(Guid MessageId, Guid AccountId, Guid FolderId, string FolderFullName, uint Uid);

public sealed record FlagUpdate(uint Uid, bool IsRead, bool IsFlagged);

public enum OperationKind
{
    MarkRead,
    MarkUnread,
    Flag,
    Unflag,
    /// <summary>Mover para a pasta Arquivo da conta.</summary>
    Archive,
    /// <summary>Mover para a Lixeira; já na Lixeira, excluir definitivamente.</summary>
    Delete,
    /// <summary>Mover para outra pasta (<see cref="PendingOperation.TargetFolder"/>), ex.: por uma regra.</summary>
    Move,
}

/// <param name="Location">Onde a mensagem estava ao enfileirar (necessário para mover/excluir, que a tiram do cache na hora).</param>
/// <param name="TargetFolder">Pasta de destino (nome completo no servidor) de <see cref="OperationKind.Move"/>.</param>
public sealed record PendingOperation(long Id, Guid AccountId, Guid MessageId, OperationKind Kind, int Attempts,
    MessageLocation? Location = null, string? TargetFolder = null);

/// <summary>Cache local (offline) de pastas, cabeçalhos, corpos e operações pendentes.</summary>
public interface IMailCache
{
    // Pastas
    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Substitui a lista de pastas da conta; pastas que sumiram do servidor são apagadas com suas mensagens.</summary>
    Task SaveFoldersAsync(Guid accountId, IReadOnlyList<MailFolder> folders, CancellationToken ct = default);

    Task<FolderSyncState?> GetSyncStateAsync(Guid folderId, CancellationToken ct = default);

    Task SetSyncStateAsync(Guid folderId, FolderSyncState state, CancellationToken ct = default);

    // Mensagens
    Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid folderId, CancellationToken ct = default);

    /// <summary>Mensagens das caixas de entrada de todas as contas, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<MessageSummary>> GetInboxMessagesAsync(CancellationToken ct = default);

    Task UpsertMessagesAsync(Guid folderId, IReadOnlyList<CachedMessage> messages, CancellationToken ct = default);

    Task<IReadOnlyList<uint>> GetUidsAsync(Guid folderId, CancellationToken ct = default);

    Task DeleteMessagesAsync(Guid folderId, IReadOnlyCollection<uint> uids, CancellationToken ct = default);

    /// <summary>Apaga todas as mensagens da pasta (ex.: UIDVALIDITY mudou).</summary>
    Task ClearFolderAsync(Guid folderId, CancellationToken ct = default);

    Task UpdateFlagsAsync(Guid folderId, IReadOnlyList<FlagUpdate> flags, CancellationToken ct = default);

    Task SetReadAsync(Guid messageId, bool isRead, CancellationToken ct = default);

    Task SetFlaggedAsync(Guid messageId, bool isFlagged, CancellationToken ct = default);

    /// <summary>Remove uma mensagem do cache (ex.: arquivada ou excluída; a pasta de destino traz a cópia ao sincronizar).</summary>
    Task DeleteMessageAsync(Guid messageId, CancellationToken ct = default);

    Task<MessageLocation?> GetLocationAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Busca de texto completo (FTS5) com filtros, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<MessageSummary>> SearchMessagesAsync(MessageSearch search, int limit, CancellationToken ct = default);

    /// <summary>Remetentes já vistos cujo nome ou endereço contém o texto, por frequência.</summary>
    Task<IReadOnlyList<MailAddress>> SearchAddressesAsync(string text, int limit, CancellationToken ct = default);

    // Corpos
    Task<MessageBody?> GetBodyAsync(Guid messageId, CancellationToken ct = default);

    Task SaveBodyAsync(MessageBody body, CancellationToken ct = default);

    /// <summary>Apaga os corpos guardados (voltam a ser baixados ao abrir cada mensagem).</summary>
    Task ClearBodiesAsync(CancellationToken ct = default);

    // Fila de operações
    Task EnqueueAsync(Guid accountId, Guid messageId, OperationKind kind, MessageLocation? location = null,
        string? targetFolder = null, CancellationToken ct = default);

    Task<IReadOnlyList<PendingOperation>> GetPendingAsync(Guid accountId, CancellationToken ct = default);

    Task CompleteAsync(long operationId, CancellationToken ct = default);

    Task RecordFailureAsync(long operationId, CancellationToken ct = default);

    /// <summary>Remove tudo que pertence à conta (pastas, mensagens, corpos, fila).</summary>
    Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default);
}

using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <summary>
/// Fonte de contas, pastas e mensagens consumida pela interface.
/// As leituras vêm do cache local (rápidas e disponíveis offline); a sincronização com o servidor
/// acontece em segundo plano e é sinalizada pelos eventos.
/// </summary>
public interface IMailboxService
{
    /// <summary>O cache de uma conta mudou; a interface deve recarregar o que estiver exibindo.</summary>
    event EventHandler<MailboxChangedEventArgs>? MailboxChanged;

    event EventHandler<AccountStatusEventArgs>? AccountStatusChanged;

    /// <summary>Uma ação otimista (ex.: marcar como lida) foi recusada pelo servidor e deve ser desfeita.</summary>
    event EventHandler<OperationFailedEventArgs>? OperationFailed;

    Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Mensagens de uma pasta, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid folderId, CancellationToken ct = default);

    /// <summary>Mensagens das caixas de entrada de todas as contas, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(CancellationToken ct = default);

    /// <summary>Corpo da mensagem: do cache, ou baixado do servidor e guardado.</summary>
    Task<MessageBody> GetMessageBodyAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Conteúdo de um anexo (decodificado), baixado do servidor sob demanda.</summary>
    Task<byte[]> GetAttachmentAsync(Guid messageId, int attachmentIndex, CancellationToken ct = default);

    // Ações: aplicadas no cache na hora e enviadas ao servidor em fila (reenviadas quando houver conexão).
    // Se o servidor recusar de vez, OperationFailed avisa para a interface desfazer.

    Task SetReadAsync(Guid messageId, bool isRead, CancellationToken ct = default);

    Task SetFlaggedAsync(Guid messageId, bool isFlagged, CancellationToken ct = default);

    /// <summary>Move para a pasta Arquivo da conta (no Gmail, "Todos os e-mails").</summary>
    Task ArchiveAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Move para a Lixeira; se já estiver nela, exclui definitivamente.</summary>
    Task DeleteAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Pede uma sincronização agora (de uma conta ou de todas).</summary>
    void RequestSync(Guid? accountId = null);

    SyncStatus GetStatus(Guid accountId);
}

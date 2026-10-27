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

    /// <summary>Aplica no cache na hora e envia ao servidor em fila (reenviada quando houver conexão).</summary>
    Task MarkAsReadAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Pede uma sincronização agora (de uma conta ou de todas).</summary>
    void RequestSync(Guid? accountId = null);

    SyncStatus GetStatus(Guid accountId);
}

using Confluir.Core.Models;
using Confluir.Core.Rules;
using Confluir.Core.Search;

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

    /// <summary>Mensagens novas (não lidas, ainda na caixa de entrada) para notificar.</summary>
    event EventHandler<MessagesArrivedEventArgs>? MessagesArrived;

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

    /// <summary>Move para outra pasta da mesma conta (nome completo no servidor).</summary>
    /// <summary>
    /// Desfaz arquivar/excluir se ainda não foi ao servidor (janela de alguns segundos): a mensagem volta ao lugar.
    /// Falso se já foi enviado.
    /// </summary>
    Task<bool> UndoRemoveAsync(MessageSummary message, CancellationToken ct = default);

    Task MoveAsync(Guid messageId, string folderFullName, CancellationToken ct = default);

    /// <summary>Aplica uma regra às mensagens que já estão na caixa de entrada; retorna quantas foram afetadas.</summary>
    Task<int> ApplyRuleAsync(MailRule rule, CancellationToken ct = default);

    /// <summary>Busca no cache local (texto completo + filtros).</summary>
    Task<IReadOnlyList<MessageSummary>> SearchAsync(MessageSearch search, int limit = 500, CancellationToken ct = default);

    /// <summary>
    /// Busca no servidor (IMAP SEARCH) numa pasta e traz para o cache o que ainda não estava nele.
    /// Retorna quantas mensagens novas entraram no cache.
    /// </summary>
    Task<int> SearchServerAsync(Guid folderId, SearchQuery query, CancellationToken ct = default);

    /// <summary>Endereços já vistos (remetentes), para autocompletar destinatários; os mais frequentes primeiro.</summary>
    Task<IReadOnlyList<MailAddress>> SearchAddressesAsync(string text, int limit = 8, CancellationToken ct = default);

    /// <summary>Pede uma sincronização agora (de uma conta ou de todas).</summary>
    void RequestSync(Guid? accountId = null);

    SyncStatus GetStatus(Guid accountId);
}

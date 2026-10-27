using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <summary>Fonte de contas, pastas e mensagens consumida pela interface.</summary>
public interface IMailboxService
{
    Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Mensagens de uma pasta, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid folderId, CancellationToken ct = default);

    /// <summary>Mensagens das caixas de entrada de todas as contas, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(CancellationToken ct = default);

    Task<MessageBody> GetMessageBodyAsync(Guid messageId, CancellationToken ct = default);
}

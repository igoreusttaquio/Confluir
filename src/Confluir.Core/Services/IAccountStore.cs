using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <summary>Contas configuradas (sem segredos).</summary>
public interface IAccountStore
{
    IReadOnlyList<MailAccount> Accounts { get; }

    event EventHandler? Changed;

    void Add(MailAccount account);

    void Remove(Guid accountId);
}

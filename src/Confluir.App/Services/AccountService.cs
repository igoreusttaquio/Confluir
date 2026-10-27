using Confluir.Core.Models;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.Services;

/// <summary>Operações sobre contas usadas por mais de uma tela.</summary>
public sealed class AccountService(IAccountStore accounts, ICredentialStore credentials, ILogger<AccountService> logger)
{
    /// <summary>Remove a conta do Confluir e apaga a senha do cofre. Nada é apagado no servidor.</summary>
    public async Task RemoveAsync(MailAccount account)
    {
        try
        {
            await credentials.DeleteAsync(CredentialKeys.Password(account.Id));
        }
        catch (Exception ex)
        {
            // A conta sai mesmo assim; o segredo órfão não é usado por mais ninguém.
            logger.LogWarning(ex, "Falha ao apagar a credencial da conta {AccountId}", account.Id);
        }
        accounts.Remove(account.Id);
        logger.LogInformation("Conta {AccountId} removida", account.Id);
    }
}

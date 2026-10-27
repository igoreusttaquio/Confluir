namespace Confluir.Core.Services;

/// <summary>
/// Segredos (senhas, tokens) guardados no cofre do sistema operacional.
/// Implementações nunca gravam segredos em texto puro.
/// </summary>
public interface ICredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string secret, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

public static class CredentialKeys
{
    public static string Password(Guid accountId) => $"account/{accountId:N}/password";
}

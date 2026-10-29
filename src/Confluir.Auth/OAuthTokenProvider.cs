using Confluir.Core.Models;
using Confluir.Core.Services;

namespace Confluir.Auth;

/// <summary>Escolhe o login (Microsoft ou Google) pelo provedor da conta.</summary>
public sealed class OAuthTokenProvider(IEnumerable<IOAuthIdentityProvider> providers) : IOAuthTokenProvider
{
    private readonly IReadOnlyList<IOAuthIdentityProvider> _providers = providers.ToList();

    private IOAuthIdentityProvider? For(MailProvider provider) => _providers.FirstOrDefault(p => p.Handles(provider));

    private IOAuthIdentityProvider Required(MailProvider provider) =>
        For(provider) ?? throw new OAuthSignInException($"Sem login OAuth para {provider}.");

    public bool IsConfigured(MailProvider provider) => For(provider)?.IsConfigured == true;

    public Task<OAuthSignIn> SignInAsync(MailProvider provider, string? loginHint, CancellationToken ct = default) =>
        Required(provider).SignInAsync(loginHint, ct);

    public Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default) =>
        Required(account.Provider).GetAccessTokenAsync(account, ct);

    public Task SignOutAsync(MailAccount account, CancellationToken ct = default) =>
        account.Auth == AuthMethod.OAuth2 && For(account.Provider) is { } provider
            ? provider.SignOutAsync(account, ct)
            : Task.CompletedTask;
}

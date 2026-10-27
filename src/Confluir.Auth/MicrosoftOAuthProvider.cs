using Confluir.Core.Models;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Confluir.Auth;

/// <summary>Configuração do app registrado no Entra ID (seção "Microsoft" do appsettings.json).</summary>
public sealed class MicrosoftAuthOptions
{
    /// <summary>ID do aplicativo (cliente). Vazio desativa o login da Microsoft.</summary>
    public string? ClientId { get; set; }

    /// <summary>"common" aceita contas pessoais (Outlook.com/Hotmail) e corporativas.</summary>
    public string Tenant { get; set; } = "common";
}

/// <summary>
/// Login da Microsoft para Outlook/Hotmail com MSAL: navegador do sistema com retorno em
/// <c>http://localhost</c>, renovação silenciosa e cache de tokens no cofre do sistema.
/// </summary>
public sealed class MicrosoftOAuthProvider : IOAuthTokenProvider
{
    // offline_access (refresh token) é pedido pelo MSAL automaticamente.
    private static readonly string[] Scopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
    ];

    private readonly MicrosoftAuthOptions _options;
    private readonly string _dataDirectory;
    private readonly ILogger<MicrosoftOAuthProvider> _logger;
    private readonly Lazy<Task<IPublicClientApplication>> _app;

    public MicrosoftOAuthProvider(MicrosoftAuthOptions options, string dataDirectory, ILogger<MicrosoftOAuthProvider> logger)
    {
        _options = options;
        _dataDirectory = dataDirectory;
        _logger = logger;
        _app = new Lazy<Task<IPublicClientApplication>>(CreateAppAsync);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ClientId);

    public async Task<OAuthSignIn> SignInAsync(string? loginHint, CancellationToken ct = default)
    {
        var app = await GetAppAsync();
        try
        {
            var builder = app.AcquireTokenInteractive(Scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount);
            if (!string.IsNullOrWhiteSpace(loginHint))
                builder = builder.WithLoginHint(loginHint.Trim());

            var result = await builder.ExecuteAsync(ct);
            _logger.LogInformation("Login da Microsoft concluído");
            return new OAuthSignIn(result.Account.HomeAccountId.Identifier, result.Account.Username, result.AccessToken);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            throw new OperationCanceledException("Login cancelado pelo usuário.", ex, ct);
        }
        catch (MsalException ex)
        {
            _logger.LogWarning(ex, "Login da Microsoft falhou ({ErrorCode})", ex.ErrorCode);
            throw new OAuthSignInException(ex.Message, ex);
        }
    }

    public async Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.OAuthAccountId))
            throw new ReauthenticationRequiredException("A conta não tem sessão da Microsoft.");

        var app = await GetAppAsync();
        var msalAccount = await app.GetAccountAsync(account.OAuthAccountId)
            ?? throw new ReauthenticationRequiredException("Sessão da Microsoft não encontrada no cache.");
        try
        {
            var result = await app.AcquireTokenSilent(Scopes, msalAccount).ExecuteAsync(ct);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            // Refresh token expirado ou revogado, senha trocada, consentimento retirado…
            throw new ReauthenticationRequiredException(ex.Message, ex);
        }
    }

    public async Task SignOutAsync(MailAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(account.OAuthAccountId))
            return;
        var app = await GetAppAsync();
        if (await app.GetAccountAsync(account.OAuthAccountId) is { } msalAccount)
            await app.RemoveAsync(msalAccount);
    }

    private Task<IPublicClientApplication> GetAppAsync() =>
        IsConfigured
            ? _app.Value
            : throw new OAuthSignInException("O login da Microsoft não está configurado (Microsoft:ClientId).");

    private async Task<IPublicClientApplication> CreateAppAsync()
    {
        var app = PublicClientApplicationBuilder.Create(_options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _options.Tenant)
            .WithRedirectUri("http://localhost")
            .Build();

        // Tokens no cofre do sistema (DPAPI/Keychain/libsecret), como as senhas.
        Directory.CreateDirectory(_dataDirectory);
        var storage = new StorageCreationPropertiesBuilder("msal.cache", _dataDirectory)
            .WithMacKeyChain("Confluir", "msal")
            .WithLinuxKeyring(
                schemaName: "com.confluir.msal",
                collection: MsalCacheHelper.LinuxKeyRingDefaultCollection,
                secretLabel: "Tokens da Microsoft do Confluir",
                attribute1: new KeyValuePair<string, string>("Product", "Confluir"),
                attribute2: new KeyValuePair<string, string>("Purpose", "msal"))
            .Build();
        var cache = await MsalCacheHelper.CreateAsync(storage);
        cache.VerifyPersistence();
        cache.RegisterCache(app.UserTokenCache);
        return app;
    }
}

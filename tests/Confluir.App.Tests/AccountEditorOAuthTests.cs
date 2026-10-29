using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.Auth;
using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confluir.App.Tests;

[Collection(nameof(GlobalCultureCollection))]
public class AccountEditorOAuthTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class FakeOAuth(bool configured, bool google = false) : IOAuthTokenProvider
    {
        public bool IsConfigured(MailProvider provider) =>
            provider == MailProvider.Outlook ? configured : provider == MailProvider.Gmail && google;
        public string? LastHint;
        public MailProvider? LastProvider;
        public Task<OAuthSignIn> SignInAsync(MailProvider provider, string? loginHint, CancellationToken ct = default)
        {
            LastHint = loginHint;
            LastProvider = provider;
            return Task.FromResult(provider == MailProvider.Gmail
                ? new OAuthSignIn("google-sub-42", loginHint ?? "ana@empresa.com.br", "token-do-google")
                : new OAuthSignIn("conta-ms-123", "ana@outlook.com", "token-de-acesso"));
        }
        public Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default) => Task.FromResult("token-renovado");
        public Task SignOutAsync(MailAccount account, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeTester : IConnectionTester
    {
        public (string Secret, AuthMethod Auth)? Last;
        public Task<ConnectionTestResult> TestAsync(MailProvider provider, ServerSettings servers, string userName, string secret,
            AuthMethod auth = AuthMethod.Password, CancellationToken ct = default)
        {
            Last = (secret, auth);
            return Task.FromResult(new ConnectionTestResult(ProtocolTestResult.Ok, ProtocolTestResult.Ok));
        }
    }

    // empresa.com.br faz de conta que está no Google Workspace (MX do Google).
    private sealed class FakeResolver : IServerSettingsResolver
    {
        public Task<ProviderPreset?> ResolveAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(KnownProviders.Detect(email)
                ?? (email.EndsWith("@empresa.com.br", StringComparison.Ordinal)
                    ? KnownProviders.FromMailExchangers(email, ["aspmx.l.google.com."])
                    : null)
                ?? KnownProviders.Guess(email));
    }

    private sealed class MemoryAccounts : IAccountStore
    {
        private readonly List<MailAccount> _items = [];
        public IReadOnlyList<MailAccount> Accounts => _items;
        public event EventHandler? Changed;
        public void Add(MailAccount account) { _items.RemoveAll(a => a.Id == account.Id); _items.Add(account); Changed?.Invoke(this, EventArgs.Empty); }
        public void Remove(Guid accountId) => _items.RemoveAll(a => a.Id == accountId);
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        public readonly Dictionary<string, string> Items = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(key));
        public Task SetAsync(string key, string secret, CancellationToken ct = default) { Items[key] = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) { Items.Remove(key); return Task.CompletedTask; }
    }

    private sealed class NoUrls : IUrlLauncher
    {
        public Task OpenAsync(string url) => Task.CompletedTask;
    }

    private static (AccountEditorViewModel Vm, FakeTester Tester, MemoryAccounts Accounts, MemoryCredentials Credentials, FakeOAuth OAuth)
        Create(bool configured, bool google = false)
    {
        var oauth = new FakeOAuth(configured, google);
        var tester = new FakeTester();
        var accounts = new MemoryAccounts();
        var credentials = new MemoryCredentials();
        var service = new AccountService(accounts, credentials, oauth, NullLogger<AccountService>.Instance);
        var vm = new AccountEditorViewModel(new FakeResolver(), tester, accounts, credentials, new NoUrls(),
            new NavigationService(), service, oauth, NullLogger<AccountEditorViewModel>.Instance);
        return (vm, tester, accounts, credentials, oauth);
    }

    private async Task TypeEmailAsync(AccountEditorViewModel vm, string email)
    {
        vm.Email = email;
        // A detecção espera o usuário parar de digitar (400 ms).
        for (var i = 0; i < 50 && (vm.IsDetecting || vm.DetectionMessage is null); i++)
            await Task.Delay(50, _ct);
    }

    [Fact]
    public async Task Outlook_sem_ID_configurado_explica_e_nao_deixa_salvar()
    {
        var (vm, _, _, _, _) = Create(configured: false);
        await TypeEmailAsync(vm, "ana@hotmail.com");

        Assert.True(vm.RequiresOAuth);
        Assert.True(vm.ShowOAuthNotConfigured);
        Assert.False(vm.ShowOAuthSignIn);
        Assert.False(vm.CanSubmit);
    }

    [Fact]
    public async Task Entrar_com_a_Microsoft_testa_com_o_token_e_salva_sem_senha()
    {
        var (vm, tester, accounts, credentials, oauth) = Create(configured: true);
        await TypeEmailAsync(vm, "ana@outlook.com");
        Assert.True(vm.ShowOAuthSignIn);
        Assert.True(vm.CanSubmit);

        await vm.SignInWithProviderCommand.ExecuteAsync(null);

        Assert.Equal("ana@outlook.com", oauth.LastHint);
        Assert.Equal("ana@outlook.com", vm.OAuthSignedInAs);
        Assert.Equal(("token-de-acesso", AuthMethod.OAuth2), tester.Last);
        Assert.True(vm.ImapStatus.IsOk);

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(accounts.Accounts);
        Assert.Equal(AuthMethod.OAuth2, saved.Auth);
        Assert.Equal("conta-ms-123", saved.OAuthAccountId);
        Assert.Equal("ana@outlook.com", saved.UserName);
        Assert.Empty(credentials.Items); // nenhuma senha gravada
    }

    [Fact]
    public async Task Sem_entrar_com_a_Microsoft_nao_salva()
    {
        var (vm, tester, accounts, _, _) = Create(configured: true);
        await TypeEmailAsync(vm, "ana@outlook.com");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(accounts.Accounts);
        Assert.Null(tester.Last);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Editar_conta_Outlook_testa_com_token_renovado()
    {
        var (vm, tester, _, _, _) = Create(configured: true);
        var account = new MailAccount(Guid.NewGuid(), "Trabalho", "ana@outlook.com", MailProvider.Outlook, "#FF7A66",
            "ana@outlook.com", KnownProviders.Outlook, AuthMethod.OAuth2, "conta-ms-123");
        vm.Load(account);
        Assert.Equal("ana@outlook.com", vm.OAuthSignedInAs);

        await vm.TestCommand.ExecuteAsync(null);

        Assert.Equal(("token-renovado", AuthMethod.OAuth2), tester.Last);
    }

    [Fact]
    public async Task Editar_a_conta_mantem_assinatura_e_nome_do_remetente()
    {
        var (vm, _, accounts, _, _) = Create(configured: true);
        var account = new MailAccount(Guid.NewGuid(), "Trabalho", "ana@outlook.com", MailProvider.Outlook, "#FF7A66",
            "ana@outlook.com", KnownProviders.Outlook, AuthMethod.OAuth2, "conta-ms-123", SenderName: "Ana Souza",
            Signature: "<div>Ana · Engenharia</div>", SignatureOnReplies: false);
        accounts.Add(account);
        vm.Load(account);
        vm.DisplayName = "Trabalho (novo nome)";

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(accounts.Accounts);
        Assert.Equal("Trabalho (novo nome)", saved.DisplayName);
        Assert.Equal(("Ana Souza", "<div>Ana · Engenharia</div>", false), (saved.SenderName, saved.Signature, saved.SignatureOnReplies));
    }

    [Fact]
    public async Task Workspace_entra_com_o_Google_por_padrao_e_salva_sem_senha()
    {
        var (vm, tester, accounts, credentials, oauth) = Create(configured: true, google: true);
        await TypeEmailAsync(vm, "ana@empresa.com.br");

        Assert.Equal(MailProvider.Gmail, vm.Provider);
        Assert.Equal("imap.gmail.com", vm.ImapHost);
        Assert.Contains("Google Workspace", vm.DetectionMessage);
        Assert.Equal("empresa.com.br", vm.DisplayName);
        Assert.True(vm.CanChooseAuth);
        Assert.True(vm.UseOAuth);
        Assert.False(vm.HasAppPasswordHint);

        await vm.SignInWithProviderCommand.ExecuteAsync(null);
        Assert.Equal(MailProvider.Gmail, oauth.LastProvider);
        Assert.Equal(("token-do-google", AuthMethod.OAuth2), tester.Last);

        await vm.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(accounts.Accounts);
        Assert.Equal((MailProvider.Gmail, AuthMethod.OAuth2, "google-sub-42"), (saved.Provider, saved.Auth, saved.OAuthAccountId));
        Assert.Empty(credentials.Items);
    }

    [Fact]
    public async Task Gmail_pode_trocar_para_senha_de_app()
    {
        var (vm, tester, accounts, credentials, _) = Create(configured: true, google: true);
        await TypeEmailAsync(vm, "ana@gmail.com");
        Assert.True(vm.UseOAuth);

        vm.UsePassword = true;
        Assert.False(vm.RequiresOAuth);
        Assert.True(vm.HasAppPasswordHint);
        vm.Password = "abcd efgh ijkl mnop";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(("abcd efgh ijkl mnop", AuthMethod.Password), tester.Last);
        var saved = Assert.Single(accounts.Accounts);
        Assert.Equal(AuthMethod.Password, saved.Auth);
        Assert.Equal("abcd efgh ijkl mnop", credentials.Items[CredentialKeys.Password(saved.Id)]);
    }

    [Fact]
    public async Task Gmail_sem_cliente_do_Google_fica_so_com_senha_de_app()
    {
        var (vm, _, _, _, _) = Create(configured: true, google: false);
        await TypeEmailAsync(vm, "ana@gmail.com");

        Assert.False(vm.CanChooseAuth);
        Assert.False(vm.RequiresOAuth);
        Assert.True(vm.HasAppPasswordHint);
        vm.UseOAuth = true; // sem o cliente, não troca
        Assert.False(vm.RequiresOAuth);
    }

    [Fact]
    public async Task Provedor_sem_ClientId_nao_esta_configurado_e_falha_ao_entrar()
    {
        var provider = new MicrosoftOAuthProvider(new MicrosoftAuthOptions(), Path.GetTempPath(),
            NullLogger<MicrosoftOAuthProvider>.Instance);

        Assert.False(provider.IsConfigured);
        await Assert.ThrowsAsync<OAuthSignInException>(() => provider.SignInAsync(null, _ct));
    }
}

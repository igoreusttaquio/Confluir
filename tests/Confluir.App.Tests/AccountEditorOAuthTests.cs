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

    private sealed class FakeOAuth(bool configured) : IOAuthTokenProvider
    {
        public bool IsConfigured => configured;
        public string? LastHint;
        public Task<OAuthSignIn> SignInAsync(string? loginHint, CancellationToken ct = default)
        {
            LastHint = loginHint;
            return Task.FromResult(new OAuthSignIn("conta-ms-123", "ana@outlook.com", "token-de-acesso"));
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

    private sealed class FakeResolver : IServerSettingsResolver
    {
        public Task<ProviderPreset?> ResolveAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(KnownProviders.Detect(email) ?? KnownProviders.Guess(email));
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
        Create(bool configured)
    {
        var oauth = new FakeOAuth(configured);
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

        await vm.SignInWithMicrosoftCommand.ExecuteAsync(null);

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
    public async Task Provedor_sem_ClientId_nao_esta_configurado_e_falha_ao_entrar()
    {
        var provider = new MicrosoftOAuthProvider(new MicrosoftAuthOptions(), Path.GetTempPath(),
            NullLogger<MicrosoftOAuthProvider>.Instance);

        Assert.False(provider.IsConfigured);
        await Assert.ThrowsAsync<OAuthSignInException>(() => provider.SignInAsync(null, _ct));
    }
}

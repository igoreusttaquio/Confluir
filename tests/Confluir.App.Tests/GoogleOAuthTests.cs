using System.Net;
using System.Text;
using Confluir.Auth;
using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using Confluir.Mail.Autoconfig;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confluir.App.Tests;

public sealed class GoogleOAuthTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly GoogleAuthOptions Options = new() { ClientId = "cliente.apps.googleusercontent.com", ClientSecret = "segredo" };

    private sealed class MemoryCredentials : ICredentialStore
    {
        public readonly Dictionary<string, string> Items = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(key));
        public Task SetAsync(string key, string secret, CancellationToken ct = default) { Items[key] = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) { Items.Remove(key); return Task.CompletedTask; }
    }

    /// <summary>Responde ao endpoint de token como o Google; guarda os formulários recebidos.</summary>
    private sealed class FakeGoogle(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<string> Forms = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Forms.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static MailAccount Account(string? subject) => new(Guid.NewGuid(), "Empresa", "ana@empresa.com.br", MailProvider.Gmail,
        "#4C6FFF", "ana@empresa.com.br", KnownProviders.Gmail, AuthMethod.OAuth2, subject);

    [Fact]
    public void Url_de_autorizacao_pede_IMAP_PKCE_e_refresh_token()
    {
        var url = GoogleOAuthProvider.BuildAuthorizeUrl("id", "http://127.0.0.1:5000/", "desafio", "estado", "ana@empresa.com.br");

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("scope=https%3A%2F%2Fmail.google.com%2F%20openid%20email", url);
        Assert.Contains("code_challenge=desafio", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2F", url);
        Assert.Contains("login_hint=ana%40empresa.com.br", url);
    }

    [Fact]
    public void Le_o_retorno_do_navegador()
    {
        var reply = GoogleOAuthProvider.ParseRedirect("GET /?state=abc&code=4%2F0Ab_x&scope=email+openid HTTP/1.1");
        Assert.Equal("abc", reply["state"]);
        Assert.Equal("4/0Ab_x", reply["code"]);
        Assert.Equal("email openid", reply["scope"]);
        Assert.Empty(GoogleOAuthProvider.ParseRedirect("GET /favicon.ico HTTP/1.1"));
    }

    [Fact]
    public void Id_token_diz_quem_entrou()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Part("{\"alg\":\"RS256\"}")}.{Part("{\"sub\":\"1098\",\"email\":\"ana@empresa.com.br\"}")}.assinatura";

        Assert.Equal(("1098", "ana@empresa.com.br"), GoogleOAuthProvider.ReadIdToken(token));
    }

    [Fact]
    public async Task Renova_o_token_com_o_refresh_guardado_e_reaproveita_ate_expirar()
    {
        var credentials = new MemoryCredentials { Items = { [GoogleOAuthProvider.RefreshTokenKey("1098")] = "refresh-1" } };
        var google = new FakeGoogle(HttpStatusCode.OK, """{"access_token":"novo","expires_in":3599,"token_type":"Bearer"}""");
        var provider = new GoogleOAuthProvider(Options, credentials, new HttpClient(google), NullLogger<GoogleOAuthProvider>.Instance);

        Assert.Equal("novo", await provider.GetAccessTokenAsync(Account("1098"), _ct));
        Assert.Equal("novo", await provider.GetAccessTokenAsync(Account("1098"), _ct));

        var form = Assert.Single(google.Forms); // a segunda veio da memória
        Assert.Contains("grant_type=refresh_token", form);
        Assert.Contains("refresh_token=refresh-1", form);
        Assert.Contains("client_secret=segredo", form);
    }

    [Fact]
    public async Task Sessao_revogada_pede_para_entrar_de_novo()
    {
        var credentials = new MemoryCredentials { Items = { [GoogleOAuthProvider.RefreshTokenKey("1098")] = "refresh-1" } };
        var google = new FakeGoogle(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""");
        var provider = new GoogleOAuthProvider(Options, credentials, new HttpClient(google), NullLogger<GoogleOAuthProvider>.Instance);

        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => provider.GetAccessTokenAsync(Account("1098"), _ct));
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => provider.GetAccessTokenAsync(Account("outro"), _ct));
    }

    [Fact]
    public async Task Sem_cliente_do_Google_nao_esta_configurado()
    {
        var provider = new GoogleOAuthProvider(new GoogleAuthOptions(), new MemoryCredentials(), new HttpClient(),
            NullLogger<GoogleOAuthProvider>.Instance);
        Assert.False(provider.IsConfigured);
        await Assert.ThrowsAsync<OAuthSignInException>(() => provider.SignInAsync(null, _ct));
    }

    [Fact]
    public void Roteador_escolhe_o_login_pelo_provedor()
    {
        var google = new GoogleOAuthProvider(Options, new MemoryCredentials(), new HttpClient(), NullLogger<GoogleOAuthProvider>.Instance);
        var microsoft = new MicrosoftOAuthProvider(new MicrosoftAuthOptions(), Path.GetTempPath(), NullLogger<MicrosoftOAuthProvider>.Instance);
        var router = new OAuthTokenProvider([microsoft, google]);

        Assert.True(router.IsConfigured(MailProvider.Gmail));
        Assert.False(router.IsConfigured(MailProvider.Outlook)); // sem ID do Entra ID
        Assert.False(router.IsConfigured(MailProvider.Yahoo));
    }

    [Theory]
    [InlineData("aspmx.l.google.com.", MailProvider.Gmail)]
    [InlineData("alt1.aspmx.l.google.com", MailProvider.Gmail)]
    [InlineData("smtp.googlemail.com.", MailProvider.Gmail)]
    [InlineData("empresa-com-br.mail.protection.outlook.com.", MailProvider.Outlook)]
    public void Dominio_proprio_pelo_MX(string mx, MailProvider expected)
    {
        var preset = KnownProviders.FromMailExchangers("ana@empresa.com.br", [mx]);
        Assert.NotNull(preset);
        Assert.Equal((expected, PresetSource.Hosted), (preset.Provider, preset.Source));
        if (expected == MailProvider.Outlook)
            Assert.Equal((AuthMethod.OAuth2, "smtp.office365.com"), (preset.Auth, preset.Servers.Smtp.Host));
    }

    [Theory]
    [InlineData("mx.empresa.com.br.")]
    [InlineData("notgoogle.com.evil.net")]
    [InlineData("fakegoogle.com")]
    public void MX_de_outros_servidores_nao_e_Google(string mx) =>
        Assert.Null(KnownProviders.FromMailExchangers("ana@empresa.com.br", [mx]));

    [Fact]
    public void Monta_a_consulta_e_le_a_resposta_DNS_com_compressao()
    {
        var query = MxLookup.BuildQuery(0xBEEF, "empresa.com.br");
        Assert.Equal([0xBE, 0xEF, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0], query[..12]);
        Assert.Equal([0, 15, 0, 1], query[^4..]); // MX, IN

        // Resposta: a pergunta copiada e duas respostas MX; a segunda aponta (compressão) para "l.google.com".
        var reply = new List<byte>(query);
        reply[2] = 0x81;
        reply[3] = 0x80; // resposta, sem erro
        reply[7] = 2; // duas respostas
        byte[] Name(params string[] labels) => [.. labels.SelectMany(l => new[] { (byte)l.Length }.Concat(Encoding.ASCII.GetBytes(l))), 0];
        void Answer(byte[] rdataName)
        {
            reply.AddRange([0xC0, 12, 0, 15, 0, 1, 0, 0, 1, 44]); // nome → pergunta; MX; IN; TTL 300
            reply.AddRange([0, (byte)(2 + rdataName.Length), 0, 5]); // tamanho, preferência
            reply.AddRange(rdataName);
        }
        var first = reply.Count + 14; // onde "aspmx" começa
        Answer(Name("aspmx", "l", "google", "com"));
        Answer([4, .. "alt1"u8.ToArray(), 0xC0, (byte)(first + 6)]); // "alt1" + ponteiro para "l.google.com"

        Assert.Equal(["aspmx.l.google.com", "alt1.l.google.com"], MxLookup.ParseResponse([.. reply], 0xBEEF));
        Assert.Null(MxLookup.ParseResponse([.. reply], 0x1234)); // resposta de outra consulta
    }

    [Fact]
    public async Task Consulta_MX_de_verdade_pelo_DNS_do_sistema()
    {
        var hosts = await MxLookup.QueryAsync("gmail.com", _ct);
        Assert.SkipWhen(hosts.Count == 0, "Sem DNS nesta máquina.");
        Assert.Contains(hosts, h => h.EndsWith("google.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Le_a_resposta_MX_do_DNS_sobre_HTTPS()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"empresa.com.br","type":15,"TTL":300,"data":"1 aspmx.l.google.com."},
              {"name":"empresa.com.br","type":15,"TTL":300,"data":"5 alt1.aspmx.l.google.com."},
              {"name":"empresa.com.br","type":46,"TTL":300,"data":"assinatura"}]}
            """;
        Assert.Equal(["aspmx.l.google.com.", "alt1.aspmx.l.google.com."], ServerSettingsResolver.ParseMx(json));
        Assert.Empty(ServerSettingsResolver.ParseMx("""{"Status":3}"""));
    }
}

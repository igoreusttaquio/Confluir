using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.Auth;

/// <summary>
/// Cliente OAuth do Google Cloud (tipo "App para computador"). Os dois valores vêm embutidos na compilação; em apps
/// instalados o "segredo" não é secreto (o Google o exige na troca do código, mas a segurança vem do PKCE).
/// </summary>
public sealed class GoogleAuthOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }
}

/// <summary>
/// "Entrar com o Google" para Gmail e Google Workspace: navegador do sistema com PKCE e retorno em
/// <c>http://127.0.0.1:&lt;porta&gt;</c> (fluxo de apps instalados do Google), renovação pelo refresh token, que fica
/// no cofre do sistema como as senhas. O token de acesso só existe em memória.
/// </summary>
public sealed class GoogleOAuthProvider(GoogleAuthOptions options, ICredentialStore credentials, HttpClient http,
    ILogger<GoogleOAuthProvider> logger) : IOAuthIdentityProvider
{
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    // mail.google.com: IMAP e SMTP (o Gmail não aceita escopos menores para IMAP). openid/email: saber quem entrou.
    private const string Scopes = "https://mail.google.com/ openid email";

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public bool Handles(MailProvider provider) => provider == MailProvider.Gmail;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ClientId) && !string.IsNullOrWhiteSpace(options.ClientSecret);

    public static string RefreshTokenKey(string subject) => $"oauth/google/{subject}/refresh";

    public async Task<OAuthSignIn> SignInAsync(string? loginHint, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = BuildAuthorizeUrl(options.ClientId!, redirectUri, challenge, state, loginHint);

        OpenBrowser(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        var reply = await ReceiveRedirectAsync(listener, timeout.Token);

        if (reply.GetValueOrDefault("state") != state)
            throw new OAuthSignInException("Resposta do Google inválida (state).");
        if (reply.TryGetValue("error", out var error))
        {
            if (error == "access_denied")
                throw new OperationCanceledException("Login cancelado pelo usuário.", ct);
            throw new OAuthSignInException(error);
        }
        if (!reply.TryGetValue("code", out var code))
            throw new OAuthSignInException("O Google não devolveu o código de autorização.");

        using var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }, ct);
        var root = tokens.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()!;
        var (subject, email) = ReadIdToken(root.GetProperty("id_token").GetString()!);
        if (root.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } refreshToken)
            await credentials.SetAsync(RefreshTokenKey(subject), refreshToken, ct);
        Remember(subject, accessToken, root);
        logger.LogInformation("Login do Google concluído");
        return new OAuthSignIn(subject, email, accessToken);
    }

    public async Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.OAuthAccountId))
            throw new ReauthenticationRequiredException("A conta não tem sessão do Google.");
        var subject = account.OAuthAccountId;
        if (_tokens.TryGetValue(subject, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Token;

        EnsureConfigured();
        // Várias conexões da mesma conta pedem ao mesmo tempo: uma renovação só.
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(subject, out cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Token;
            var refreshToken = await credentials.GetAsync(RefreshTokenKey(subject), ct)
                ?? throw new ReauthenticationRequiredException("Sessão do Google não encontrada no cofre.");
            JsonDocument tokens;
            try
            {
                tokens = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                }, ct);
            }
            catch (OAuthSignInException ex) when (ex.Message.Contains("invalid_grant", StringComparison.Ordinal))
            {
                // Revogado, senha trocada, ou (app em teste no Google Cloud) expirado depois de 7 dias.
                throw new ReauthenticationRequiredException(ex.Message, ex);
            }
            using (tokens)
            {
                var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
                Remember(subject, accessToken, tokens.RootElement);
                return accessToken;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task SignOutAsync(MailAccount account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.OAuthAccountId))
            return;
        var key = RefreshTokenKey(account.OAuthAccountId);
        _tokens.TryRemove(account.OAuthAccountId, out _);
        if (await credentials.GetAsync(key, ct) is { } refreshToken)
        {
            try
            {
                // Revoga no Google também (some de "Apps com acesso à sua conta").
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken });
                using var response = await http.PostAsync(RevokeEndpoint, content, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Não foi possível revogar o token do Google");
            }
        }
        await credentials.DeleteAsync(key, ct);
    }

    // Partes puras (testadas) ------------------------------------------------------------------------

    public static string BuildAuthorizeUrl(string clientId, string redirectUri, string challenge, string state, string? loginHint)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            // offline + consent: o Google só manda o refresh token assim (sem ele, seria preciso entrar toda hora).
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
        };
        if (!string.IsNullOrWhiteSpace(loginHint))
            query["login_hint"] = loginHint.Trim();
        return AuthorizeEndpoint + "?" + string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    /// <summary>Parâmetros da primeira linha de uma requisição HTTP ("GET /?code=…&amp;state=… HTTP/1.1").</summary>
    public static Dictionary<string, string> ParseRedirect(string requestLine)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[1].IndexOf('?') is var q && q < 0)
            return result;
        foreach (var pair in parts[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    /// <summary>Quem entrou: "sub" (identificador estável) e "email" do id_token (veio direto do Google, por TLS).</summary>
    public static (string Subject, string Email) ReadIdToken(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2)
            throw new OAuthSignInException("id_token inválido.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var subject = doc.RootElement.GetProperty("sub").GetString() ?? throw new OAuthSignInException("id_token sem sub.");
        var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        return (subject, email);
    }

    // ------------------------------------------------------------------------------------------------

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new OAuthSignInException("Login do Google indisponível nesta compilação (sem cliente OAuth do Google Cloud).");
    }

    private void Remember(string subject, string accessToken, JsonElement root)
    {
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        _tokens[subject] = (accessToken, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds) - ExpiryMargin);
    }

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = options.ClientId!;
        form["client_secret"] = options.ClientSecret!;
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(TokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google recusou o token ({Status}): {Body}", (int)response.StatusCode, body);
            // Ex.: {"error": "invalid_grant", "error_description": "Token has been expired or revoked."}
            string message;
            try
            {
                using var error = JsonDocument.Parse(body);
                message = error.RootElement.TryGetProperty("error", out var code) ? code.GetString() ?? body : body;
                if (error.RootElement.TryGetProperty("error_description", out var description))
                    message += ": " + description.GetString();
            }
            catch (JsonException)
            {
                message = $"HTTP {(int)response.StatusCode}";
            }
            throw new OAuthSignInException(message);
        }
        return JsonDocument.Parse(body);
    }

    /// <summary>Espera o navegador voltar para a porta local e responde com uma página "pode voltar ao Confluir".</summary>
    private static async Task<Dictionary<string, string>> ReceiveRedirectAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            var parameters = ParseRedirect(requestLine);
            // O navegador às vezes pede /favicon.ico antes: só a resposta com state conta.
            var done = parameters.ContainsKey("state");
            var html = done
                ? "<!doctype html><meta charset=utf-8><title>Confluir</title><body style=\"font-family:system-ui;text-align:center;padding-top:80px\">" +
                  "<h2>Pronto!</h2><p>Você já pode fechar esta aba e voltar para o Confluir.</p>"
                : "";
            var bytes = Encoding.UTF8.GetBytes(html);
            var header = $"HTTP/1.1 {(done ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                         $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(bytes, ct);
            if (done)
                return parameters;
        }
    }

    private static void OpenBrowser(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", [url]);
        else
            Process.Start("xdg-open", [url]);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

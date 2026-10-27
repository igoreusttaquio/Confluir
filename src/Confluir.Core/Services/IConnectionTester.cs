using Confluir.Core.Models;
using Confluir.Core.Providers;

namespace Confluir.Core.Services;

public enum ConnectionError
{
    None,
    AuthenticationFailed,
    AppPasswordRequired,
    ServerUnreachable,
    SecureConnectionFailed,
    Timeout,
    Canceled,
    /// <summary>Sessão OAuth expirada/revogada: entrar de novo com o provedor.</summary>
    ReauthenticationRequired,
    Unknown,
}

public sealed record ProtocolTestResult(ConnectionError Error, string? ServerMessage = null)
{
    public bool Success => Error == ConnectionError.None;

    public static readonly ProtocolTestResult Ok = new(ConnectionError.None);
}

/// <param name="SmtpAlternative">
/// Preenchido quando o SMTP configurado não respondeu, mas a porta alternativa funcionou;
/// a conta deve passar a usar este endpoint.
/// </param>
public sealed record ConnectionTestResult(
    ProtocolTestResult Imap, ProtocolTestResult Smtp, ServerEndpoint? SmtpAlternative = null)
{
    public bool Success => Imap.Success && Smtp.Success;
}

/// <summary>Valida IMAP e SMTP com as credenciais antes de salvar uma conta.</summary>
public interface IConnectionTester
{
    /// <param name="secret">Senha, ou o token de acesso quando <paramref name="auth"/> é OAuth2.</param>
    Task<ConnectionTestResult> TestAsync(
        MailProvider provider, ServerSettings servers, string userName, string secret,
        AuthMethod auth = AuthMethod.Password, CancellationToken ct = default);
}

/// <summary>Sugere servidores para um endereço: provedores conhecidos, autoconfiguração ou palpite.</summary>
public interface IServerSettingsResolver
{
    Task<ProviderPreset?> ResolveAsync(string email, CancellationToken ct = default);
}

using System.Net.Sockets;
using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit;
using MailKit.Security;

namespace Confluir.Mail;

/// <summary>Traduz exceções de MailKit/rede em categorias de erro que a interface explica ao usuário.</summary>
public static class MailErrorClassifier
{
    public static ProtocolTestResult Classify(Exception ex, MailProvider provider, CancellationToken ct = default)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
            return new(ConnectionError.Canceled);

        return ex switch
        {
            ReauthenticationRequiredException reauth => new(ConnectionError.ReauthenticationRequired, reauth.Message),
            AuthenticationException auth => new(ClassifyAuthentication(auth.Message, provider), auth.Message),
            // O Gmail recusa credenciais no SMTP com "5.7.8 ... not accepted" e fecha a conexão;
            // o MailKit reporta isso como erro de protocolo, não de autenticação.
            ProtocolException or CommandException when IsCredentialRejection(ex.Message) =>
                new(ClassifyAuthentication(ex.Message, provider), ex.Message),
            SslHandshakeException ssl => new(ConnectionError.SecureConnectionFailed, ssl.Message),
            SocketException socket => new(ClassifySocket(socket), socket.Message),
            TimeoutException or OperationCanceledException => new(ConnectionError.Timeout),
            IOException { InnerException: { } inner } => Classify(inner, provider, ct),
            _ => new(ConnectionError.Unknown, ex.Message),
        };
    }

    private static ConnectionError ClassifyAuthentication(string serverMessage, MailProvider provider)
    {
        // Gmail responde explicitamente (IMAP "[ALERT] Application-specific password required", SMTP "5.7.9 ...").
        if (serverMessage.Contains("application-specific password", StringComparison.OrdinalIgnoreCase))
            return ConnectionError.AppPasswordRequired;
        // Gmail e Yahoo recusam a senha normal da conta; a causa quase sempre é a falta de senha de app.
        return provider is MailProvider.Gmail or MailProvider.Yahoo
            ? ConnectionError.AppPasswordRequired
            : ConnectionError.AuthenticationFailed;
    }

    private static bool IsCredentialRejection(string message) =>
        message.Contains("5.7.8", StringComparison.Ordinal) ||
        message.Contains("535", StringComparison.Ordinal) ||
        message.Contains("BadCredentials", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("not accepted", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase);

    private static ConnectionError ClassifySocket(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.TimedOut => ConnectionError.Timeout,
        _ => ConnectionError.ServerUnreachable,
    };
}

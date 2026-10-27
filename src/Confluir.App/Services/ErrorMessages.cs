using Confluir.App.Resources;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Mail;

namespace Confluir.App.Services;

/// <summary>Textos amigáveis para erros de conexão.</summary>
public static class ErrorMessages
{
    public static string For(ProtocolTestResult result) => result.Error switch
    {
        ConnectionError.None => Strings.Test_Ok,
        ConnectionError.AuthenticationFailed => Strings.Error_AuthenticationFailed,
        ConnectionError.AppPasswordRequired => Strings.Error_AppPasswordRequired,
        ConnectionError.ServerUnreachable => Strings.Error_ServerUnreachable,
        ConnectionError.SecureConnectionFailed => Strings.Error_SecureConnectionFailed,
        ConnectionError.Timeout => Strings.Error_Timeout,
        ConnectionError.Canceled => Strings.Error_Canceled,
        ConnectionError.ReauthenticationRequired => Strings.Error_ReauthenticationRequired,
        _ => string.Format(Strings.Error_Unknown, result.ServerMessage ?? "?"),
    };

    public static string For(Exception ex, MailProvider provider = MailProvider.Imap) =>
        For(MailErrorClassifier.Classify(ex, provider));
}

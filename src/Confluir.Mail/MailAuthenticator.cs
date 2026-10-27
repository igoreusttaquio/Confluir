using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit;
using MailKit.Security;

namespace Confluir.Mail;

/// <summary>Autentica IMAP/SMTP com senha (no cofre do sistema) ou com token OAuth2 (XOAUTH2).</summary>
public sealed class MailAuthenticator(ICredentialStore credentials, IOAuthTokenProvider oauth)
{
    public async Task AuthenticateAsync(IMailService client, MailAccount account, CancellationToken ct)
    {
        if (account.Auth == AuthMethod.OAuth2)
        {
            var token = await oauth.GetAccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.UserName, token), ct);
            return;
        }

        var password = await credentials.GetAsync(CredentialKeys.Password(account.Id), ct)
            ?? throw new AuthenticationException("A senha desta conta não foi encontrada no cofre do sistema.");
        // Sem token, o MailKit não deve tentar XOAUTH2 com a senha.
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        await client.AuthenticateAsync(account.UserName, password, ct);
    }

    /// <summary>Para o teste de conexão: o segredo ainda não está salvo (senha digitada ou token recém-obtido).</summary>
    public static Task AuthenticateAsync(IMailService client, string userName, string secret, AuthMethod auth, CancellationToken ct)
    {
        if (auth == AuthMethod.OAuth2)
            return client.AuthenticateAsync(new SaslMechanismOAuth2(userName, secret), ct);
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        return client.AuthenticateAsync(userName, secret, ct);
    }
}

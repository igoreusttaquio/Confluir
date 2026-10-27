using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;

namespace Confluir.Mail;

/// <summary>Conecta e autentica em IMAP e SMTP (em paralelo) sem guardar nada.</summary>
public sealed class ConnectionTester(ILogger<ConnectionTester> logger) : IConnectionTester
{
    private const int TimeoutMs = 20_000;

    public async Task<ConnectionTestResult> TestAsync(
        MailProvider provider, ServerSettings servers, string userName, string secret,
        AuthMethod auth = AuthMethod.Password, CancellationToken ct = default)
    {
        var imap = TestAsync("IMAP", servers.Imap, provider, ct, async token =>
        {
            using var client = new ImapClient { Timeout = TimeoutMs };
            await AuthenticateAsync(client, servers.Imap, userName, secret, auth, token);
        });
        var smtp = TestSmtpAsync(servers.Smtp, provider, userName, secret, auth, ct);

        var smtpResult = await smtp;
        ServerEndpoint? alternative = null;
        if (IsReachabilityError(smtpResult) && KnownProviders.AlternativeSmtp(servers.Smtp) is { } alt)
        {
            logger.LogInformation("SMTP {Port} sem resposta; tentando a porta {AltPort}", servers.Smtp.Port, alt.Port);
            var altResult = await TestSmtpAsync(alt, provider, userName, secret, auth, ct);
            if (altResult.Success)
            {
                smtpResult = altResult;
                alternative = alt;
            }
        }
        return new ConnectionTestResult(await imap, smtpResult, alternative);
    }

    private Task<ProtocolTestResult> TestSmtpAsync(
        ServerEndpoint endpoint, MailProvider provider, string userName, string secret, AuthMethod auth, CancellationToken ct) =>
        TestAsync("SMTP", endpoint, provider, ct, async token =>
        {
            using var client = new SmtpClient { Timeout = TimeoutMs };
            await AuthenticateAsync(client, endpoint, userName, secret, auth, token);
        });

    private static bool IsReachabilityError(ProtocolTestResult r) =>
        r.Error is ConnectionError.Timeout or ConnectionError.ServerUnreachable or ConnectionError.SecureConnectionFailed;

    private static async Task AuthenticateAsync(
        MailService client, ServerEndpoint endpoint, string userName, string secret, AuthMethod auth, CancellationToken ct)
    {
        await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security.ToSocketOptions(), ct);
        await MailAuthenticator.AuthenticateAsync(client, userName, secret, auth, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    private async Task<ProtocolTestResult> TestAsync(
        string protocol, ServerEndpoint endpoint, MailProvider provider, CancellationToken ct, Func<CancellationToken, Task> test)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await test(ct);
            logger.LogInformation("Teste {Protocol} em {Host}:{Port}: ok em {ElapsedMs} ms",
                protocol, endpoint.Host, endpoint.Port, started.ElapsedMilliseconds);
            return ProtocolTestResult.Ok;
        }
        catch (Exception ex)
        {
            var result = MailErrorClassifier.Classify(ex, provider, ct);
            logger.LogInformation("Teste {Protocol} em {Host}:{Port}: {Error} em {ElapsedMs} ms ({Exception})",
                protocol, endpoint.Host, endpoint.Port, result.Error, started.ElapsedMilliseconds, ex.GetType().Name);
            return result;
        }
    }
}

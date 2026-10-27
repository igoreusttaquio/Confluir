using System.Net.Sockets;
using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit.Security;

namespace Confluir.Mail.Tests;

public class MailErrorClassifierTests
{
    [Fact]
    public void Gmail_pedindo_senha_de_app_explicitamente()
    {
        var ex = new AuthenticationException("[ALERT] Application-specific password required: https://support.google.com/accounts/answer/185833 (Failure)");
        var result = MailErrorClassifier.Classify(ex, MailProvider.Imap, TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionError.AppPasswordRequired, result.Error);
        Assert.Contains("Application-specific", result.ServerMessage);
    }

    [Theory]
    [InlineData(MailProvider.Gmail, ConnectionError.AppPasswordRequired)]
    [InlineData(MailProvider.Yahoo, ConnectionError.AppPasswordRequired)]
    [InlineData(MailProvider.Imap, ConnectionError.AuthenticationFailed)]
    public void Credencial_recusada_depende_do_provedor(MailProvider provider, ConnectionError expected)
    {
        var ex = new AuthenticationException("[AUTHENTICATIONFAILED] Invalid credentials");
        Assert.Equal(expected, MailErrorClassifier.Classify(ex, provider, TestContext.Current.CancellationToken).Error);
    }

    [Theory]
    [InlineData(MailProvider.Gmail, ConnectionError.AppPasswordRequired)]
    [InlineData(MailProvider.Imap, ConnectionError.AuthenticationFailed)]
    public void Gmail_SMTP_recusa_credencial_derrubando_a_conexao(MailProvider provider, ConnectionError expected)
    {
        // Resposta real do smtp.gmail.com a usuário/senha inválidos (out/2026).
        var ex = new MailKit.Net.Smtp.SmtpProtocolException(
            "The SMTP server has unexpectedly disconnected: 5.7.8 Username and Password not accepted. " +
            "For more information, go to\n5.7.8  https://support.google.com/mail/?p=BadCredentials - gsmtp");
        Assert.Equal(expected, MailErrorClassifier.Classify(ex, provider, TestContext.Current.CancellationToken).Error);
    }

    [Fact]
    public void Erro_de_protocolo_sem_relacao_com_credencial_continua_desconhecido()
    {
        var ex = new MailKit.Net.Imap.ImapProtocolException("Syntax error in server response");
        Assert.Equal(ConnectionError.Unknown, MailErrorClassifier.Classify(ex, MailProvider.Gmail, TestContext.Current.CancellationToken).Error);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, ConnectionError.ServerUnreachable)]
    [InlineData(SocketError.ConnectionRefused, ConnectionError.ServerUnreachable)]
    [InlineData(SocketError.TimedOut, ConnectionError.Timeout)]
    public void Erros_de_rede(SocketError code, ConnectionError expected)
    {
        Assert.Equal(expected, MailErrorClassifier.Classify(new SocketException((int)code), MailProvider.Imap, TestContext.Current.CancellationToken).Error);
    }

    [Fact]
    public void Erro_de_rede_embrulhado_em_IOException()
    {
        var ex = new IOException("falha", new SocketException((int)SocketError.ConnectionReset));
        Assert.Equal(ConnectionError.ServerUnreachable, MailErrorClassifier.Classify(ex, MailProvider.Imap, TestContext.Current.CancellationToken).Error);
    }

    [Fact]
    public void Falha_de_TLS()
    {
        var ex = new SslHandshakeException("certificado inválido");
        Assert.Equal(ConnectionError.SecureConnectionFailed, MailErrorClassifier.Classify(ex, MailProvider.Imap, TestContext.Current.CancellationToken).Error);
    }

    [Fact]
    public void Cancelamento_pelo_usuario_nao_e_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new OperationCanceledException(cts.Token);

        Assert.Equal(ConnectionError.Canceled, MailErrorClassifier.Classify(ex, MailProvider.Imap, cts.Token).Error);
        Assert.Equal(ConnectionError.Timeout, MailErrorClassifier.Classify(new TimeoutException(), MailProvider.Imap, TestContext.Current.CancellationToken).Error);
    }
}

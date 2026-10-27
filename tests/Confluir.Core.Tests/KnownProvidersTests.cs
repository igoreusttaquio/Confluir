using Confluir.Core.Models;
using Confluir.Core.Providers;

namespace Confluir.Core.Tests;

public class KnownProvidersTests
{
    [Theory]
    [InlineData("ana@gmail.com", MailProvider.Gmail, "imap.gmail.com", "smtp.gmail.com")]
    [InlineData("ana@GoogleMail.com", MailProvider.Gmail, "imap.gmail.com", "smtp.gmail.com")]
    [InlineData("ana@yahoo.com.br", MailProvider.Yahoo, "imap.mail.yahoo.com", "smtp.mail.yahoo.com")]
    [InlineData("ana@ymail.com", MailProvider.Yahoo, "imap.mail.yahoo.com", "smtp.mail.yahoo.com")]
    [InlineData("ana@hotmail.com", MailProvider.Outlook, "outlook.office365.com", "smtp-mail.outlook.com")]
    [InlineData(" ana@outlook.com.br ", MailProvider.Outlook, "outlook.office365.com", "smtp-mail.outlook.com")]
    public void Detecta_provedor_pelo_dominio(string email, MailProvider provider, string imap, string smtp)
    {
        var preset = KnownProviders.Detect(email);

        Assert.NotNull(preset);
        Assert.Equal(provider, preset.Provider);
        Assert.Equal(imap, preset.Servers.Imap.Host);
        Assert.Equal(smtp, preset.Servers.Smtp.Host);
        Assert.Equal(email.Trim(), preset.UserName);
        Assert.Equal(PresetSource.Known, preset.Source);
    }

    [Fact]
    public void Outlook_exige_OAuth2_e_os_demais_usam_senha()
    {
        Assert.Equal(AuthMethod.OAuth2, KnownProviders.Detect("a@live.com")!.Auth);
        Assert.Equal(AuthMethod.Password, KnownProviders.Detect("a@gmail.com")!.Auth);
        Assert.Equal(AuthMethod.Password, KnownProviders.Detect("a@yahoo.com")!.Auth);
    }

    [Fact]
    public void Outlook_envia_por_587_com_STARTTLS()
    {
        var smtp = KnownProviders.Outlook.Smtp;
        Assert.Equal(587, smtp.Port);
        Assert.Equal(SecurityMode.StartTls, smtp.Security);
    }

    [Fact]
    public void SMTP_alterna_entre_465_SSL_e_587_STARTTLS()
    {
        var ssl = new ServerEndpoint("smtp.gmail.com", 465, SecurityMode.SslOnConnect);
        var startTls = new ServerEndpoint("smtp.gmail.com", 587, SecurityMode.StartTls);

        Assert.Equal(startTls, KnownProviders.AlternativeSmtp(ssl));
        Assert.Equal(ssl, KnownProviders.AlternativeSmtp(startTls));
        Assert.Null(KnownProviders.AlternativeSmtp(new ServerEndpoint("smtp.x.com", 2525, SecurityMode.StartTls)));
    }

    [Fact]
    public void Dominio_desconhecido_nao_e_detectado_mas_gera_palpite()
    {
        Assert.Null(KnownProviders.Detect("joao@empresa.com.br"));

        var guess = KnownProviders.Guess("joao@empresa.com.br");
        Assert.NotNull(guess);
        Assert.Equal(MailProvider.Imap, guess.Provider);
        Assert.Equal(PresetSource.Guess, guess.Source);
        Assert.Equal(new ServerEndpoint("imap.empresa.com.br", 993, SecurityMode.SslOnConnect), guess.Servers.Imap);
        Assert.Equal(new ServerEndpoint("smtp.empresa.com.br", 465, SecurityMode.SslOnConnect), guess.Servers.Smtp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    [InlineData("@gmail.com")]
    [InlineData("ana@")]
    [InlineData("ana@localhost")]
    [InlineData("ana@.com")]
    public void Endereco_invalido_nao_gera_configuracao(string email)
    {
        Assert.Null(KnownProviders.GetDomain(email));
        Assert.Null(KnownProviders.Detect(email));
        Assert.Null(KnownProviders.Guess(email));
    }
}

using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Mail.Autoconfig;

namespace Confluir.Mail.Tests;

public class IspdbParserTests
{
    // Formato real da ISPDB (servidores fictícios).
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <clientConfig version="1.1">
          <emailProvider id="exemplo.com.br">
            <domain>exemplo.com.br</domain>
            <incomingServer type="pop3">
              <hostname>pop.exemplo.com.br</hostname><port>995</port><socketType>SSL</socketType>
              <username>%EMAILADDRESS%</username><authentication>password-cleartext</authentication>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imap.exemplo.com.br</hostname><port>143</port><socketType>STARTTLS</socketType>
              <username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imap.exemplo.com.br</hostname><port>993</port><socketType>SSL</socketType>
              <username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication>
            </incomingServer>
            <outgoingServer type="smtp">
              <hostname>smtp.exemplo.com.br</hostname><port>587</port><socketType>STARTTLS</socketType>
              <username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication>
            </outgoingServer>
          </emailProvider>
        </clientConfig>
        """;

    [Fact]
    public void Le_servidores_preferindo_SSL()
    {
        var preset = IspdbParser.Parse(Sample, "joana@exemplo.com.br");

        Assert.NotNull(preset);
        Assert.Equal(new ServerEndpoint("imap.exemplo.com.br", 993, SecurityMode.SslOnConnect), preset.Servers.Imap);
        Assert.Equal(new ServerEndpoint("smtp.exemplo.com.br", 587, SecurityMode.StartTls), preset.Servers.Smtp);
        Assert.Equal(AuthMethod.Password, preset.Auth);
        Assert.Equal(PresetSource.Autoconfig, preset.Source);
    }

    [Fact]
    public void Expande_modelo_de_usuario()
    {
        Assert.Equal("joana", IspdbParser.Parse(Sample, "joana@exemplo.com.br")!.UserName);
    }

    [Fact]
    public void Somente_OAuth2_vira_metodo_OAuth2()
    {
        var xml = Sample.Replace("password-cleartext", "OAuth2");
        Assert.Equal(AuthMethod.OAuth2, IspdbParser.Parse(xml, "joana@exemplo.com.br")!.Auth);
    }

    [Theory]
    [InlineData("não é xml")]
    [InlineData("<clientConfig/>")]
    [InlineData("<clientConfig><emailProvider><incomingServer type=\"imap\"><hostname>x</hostname><port>993</port></incomingServer></emailProvider></clientConfig>")]
    public void Conteudo_invalido_ou_incompleto_retorna_nulo(string xml)
    {
        Assert.Null(IspdbParser.Parse(xml, "a@b.com"));
    }
}

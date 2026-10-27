using System.Xml;
using System.Xml.Linq;
using Confluir.Core.Models;
using Confluir.Core.Providers;

namespace Confluir.Mail.Autoconfig;

/// <summary>
/// Lê o formato "clientConfig" da ISPDB do Thunderbird
/// (https://wiki.mozilla.org/Thunderbird:Autoconfiguration:ConfigFileFormat).
/// </summary>
public static class IspdbParser
{
    public static ProviderPreset? Parse(string xml, string email)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }

        var provider = doc.Root?.Element("emailProvider");
        if (provider is null)
            return null;

        var imap = Best(provider.Elements("incomingServer").Where(e => (string?)e.Attribute("type") == "imap"));
        var smtp = Best(provider.Elements("outgoingServer").Where(e => (string?)e.Attribute("type") == "smtp"));
        if (imap is null || smtp is null)
            return null;

        var auth = UsesPassword(imap.Value.Element) ? AuthMethod.Password : AuthMethod.OAuth2;
        var userName = Expand((string?)imap.Value.Element.Element("username") ?? "%EMAILADDRESS%", email);

        return new ProviderPreset(
            MailProvider.Imap,
            new ServerSettings(imap.Value.Endpoint, smtp.Value.Endpoint),
            auth,
            PresetSource.Autoconfig,
            userName);
    }

    /// <summary>Prefere SSL, depois STARTTLS; texto puro só se for a única opção.</summary>
    private static (XElement Element, ServerEndpoint Endpoint)? Best(IEnumerable<XElement> servers) =>
        servers
            .Select(e => (Element: e, Endpoint: ToEndpoint(e)))
            .Where(x => x.Endpoint is not null)
            .Select(x => (x.Element, Endpoint: x.Endpoint!))
            .OrderBy(x => x.Endpoint.Security switch
            {
                SecurityMode.SslOnConnect => 0,
                SecurityMode.StartTls => 1,
                _ => 2,
            })
            .ThenBy(x => UsesPassword(x.Element) ? 0 : 1)
            .Cast<(XElement, ServerEndpoint)?>()
            .FirstOrDefault();

    private static ServerEndpoint? ToEndpoint(XElement server)
    {
        var host = (string?)server.Element("hostname");
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse((string?)server.Element("port"), out var port))
            return null;
        var security = ((string?)server.Element("socketType"))?.Trim().ToUpperInvariant() switch
        {
            "SSL" => SecurityMode.SslOnConnect,
            "STARTTLS" => SecurityMode.StartTls,
            _ => SecurityMode.None,
        };
        return new ServerEndpoint(host.Trim(), port, security);
    }

    private static bool UsesPassword(XElement server) =>
        server.Elements("authentication").Any(a => a.Value.StartsWith("password", StringComparison.OrdinalIgnoreCase));

    private static string Expand(string template, string email)
    {
        var address = email.Trim();
        var at = address.LastIndexOf('@');
        return template
            .Replace("%EMAILADDRESS%", address)
            .Replace("%EMAILLOCALPART%", at > 0 ? address[..at] : address)
            .Replace("%EMAILDOMAIN%", at > 0 ? address[(at + 1)..] : "");
    }
}

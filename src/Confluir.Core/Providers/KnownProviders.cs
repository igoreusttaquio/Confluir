using Confluir.Core.Models;

namespace Confluir.Core.Providers;

/// <summary>Configuração sugerida para um endereço de email.</summary>
public sealed record ProviderPreset(
    MailProvider Provider,
    ServerSettings Servers,
    AuthMethod Auth,
    PresetSource Source,
    string UserName);

public enum PresetSource
{
    /// <summary>Provedor conhecido, com servidores fixos.</summary>
    Known,

    /// <summary>Encontrado na base pública de configuração do Thunderbird (ISPDB).</summary>
    Autoconfig,

    /// <summary>Palpite (imap.&lt;domínio&gt; / smtp.&lt;domínio&gt;); o usuário deve conferir.</summary>
    Guess,

    /// <summary>Domínio próprio hospedado no Google Workspace ou no Microsoft 365 (descoberto pelo registro MX).</summary>
    Hosted,
}

/// <summary>
/// Servidores dos provedores conhecidos, conferidos na documentação oficial (out/2026):
/// Gmail (support.google.com/mail/answer/7126229), Yahoo (help.yahoo.com/kb/SLN4075.html),
/// Outlook.com (support.microsoft.com, "POP, IMAP, and SMTP settings for Outlook.com").
/// </summary>
public static class KnownProviders
{
    public static readonly ServerSettings Gmail = new(
        new ServerEndpoint("imap.gmail.com", 993, SecurityMode.SslOnConnect),
        new ServerEndpoint("smtp.gmail.com", 465, SecurityMode.SslOnConnect));

    public static readonly ServerSettings Yahoo = new(
        new ServerEndpoint("imap.mail.yahoo.com", 993, SecurityMode.SslOnConnect),
        new ServerEndpoint("smtp.mail.yahoo.com", 465, SecurityMode.SslOnConnect));

    public static readonly ServerSettings Outlook = new(
        new ServerEndpoint("outlook.office365.com", 993, SecurityMode.SslOnConnect),
        new ServerEndpoint("smtp-mail.outlook.com", 587, SecurityMode.StartTls));

    /// <summary>Contas corporativas do Microsoft 365 (Exchange Online).</summary>
    public static readonly ServerSettings Microsoft365 = new(
        new ServerEndpoint("outlook.office365.com", 993, SecurityMode.SslOnConnect),
        new ServerEndpoint("smtp.office365.com", 587, SecurityMode.StartTls));

    private static readonly Dictionary<string, MailProvider> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = MailProvider.Gmail,
        ["googlemail.com"] = MailProvider.Gmail,

        ["yahoo.com"] = MailProvider.Yahoo,
        ["yahoo.com.br"] = MailProvider.Yahoo,
        ["ymail.com"] = MailProvider.Yahoo,
        ["rocketmail.com"] = MailProvider.Yahoo,

        ["outlook.com"] = MailProvider.Outlook,
        ["outlook.com.br"] = MailProvider.Outlook,
        ["hotmail.com"] = MailProvider.Outlook,
        ["hotmail.com.br"] = MailProvider.Outlook,
        ["live.com"] = MailProvider.Outlook,
        ["msn.com"] = MailProvider.Outlook,
    };

    /// <summary>Domínio de um endereço de email, ou nulo se o endereço for inválido.</summary>
    public static string? GetDomain(string email)
    {
        var s = email.Trim();
        var at = s.LastIndexOf('@');
        if (at <= 0 || at == s.Length - 1)
            return null;
        var domain = s[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') ? domain.ToLowerInvariant() : null;
    }

    /// <summary>Configuração de um provedor conhecido pelo domínio, ou nulo se o domínio não for conhecido.</summary>
    public static ProviderPreset? Detect(string email)
    {
        var domain = GetDomain(email);
        if (domain is null || !Domains.TryGetValue(domain, out var provider))
            return null;

        var address = email.Trim();
        return provider switch
        {
            MailProvider.Gmail => new(provider, Gmail, AuthMethod.Password, PresetSource.Known, address),
            MailProvider.Yahoo => new(provider, Yahoo, AuthMethod.Password, PresetSource.Known, address),
            _ => new(MailProvider.Outlook, Outlook, AuthMethod.OAuth2, PresetSource.Known, address),
        };
    }

    /// <summary>
    /// Domínio próprio pelo registro MX: Google Workspace (aspmx.l.google.com, …google.com/googlemail.com) usa os
    /// servidores do Gmail; Microsoft 365 (…mail.protection.outlook.com) os do Exchange Online, com login da
    /// Microsoft. Nulo para os demais.
    /// </summary>
    public static ProviderPreset? FromMailExchangers(string email, IEnumerable<string> mxHosts)
    {
        if (GetDomain(email) is null)
            return null;
        var hosts = mxHosts.Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()).ToList();
        static bool Under(string host, string zone) => host == zone || host.EndsWith("." + zone, StringComparison.Ordinal);
        var address = email.Trim();
        if (hosts.Any(h => Under(h, "google.com") || Under(h, "googlemail.com")))
            return new ProviderPreset(MailProvider.Gmail, Gmail, AuthMethod.Password, PresetSource.Hosted, address);
        if (hosts.Any(h => Under(h, "mail.protection.outlook.com")))
            return new ProviderPreset(MailProvider.Outlook, Microsoft365, AuthMethod.OAuth2, PresetSource.Hosted, address);
        return null;
    }

    /// <summary>
    /// Alternativa padrão de envio no mesmo servidor: 465/SSL ↔ 587/STARTTLS.
    /// Útil quando uma rede bloqueia uma das portas (comum com a 465 em redes corporativas).
    /// </summary>
    public static ServerEndpoint? AlternativeSmtp(ServerEndpoint smtp) => (smtp.Port, smtp.Security) switch
    {
        (465, SecurityMode.SslOnConnect) => smtp with { Port = 587, Security = SecurityMode.StartTls },
        (587, SecurityMode.StartTls) => smtp with { Port = 465, Security = SecurityMode.SslOnConnect },
        _ => null,
    };

    /// <summary>Palpite para domínios desconhecidos: imap.&lt;domínio&gt;:993 e smtp.&lt;domínio&gt;:465, ambos com SSL.</summary>
    public static ProviderPreset? Guess(string email)
    {
        var domain = GetDomain(email);
        if (domain is null)
            return null;
        var servers = new ServerSettings(
            new ServerEndpoint($"imap.{domain}", 993, SecurityMode.SslOnConnect),
            new ServerEndpoint($"smtp.{domain}", 465, SecurityMode.SslOnConnect));
        return new ProviderPreset(MailProvider.Imap, servers, AuthMethod.Password, PresetSource.Guess, email.Trim());
    }
}

namespace Confluir.Core.Models;

public enum SecurityMode
{
    /// <summary>TLS desde a conexão (ex.: IMAP 993, SMTP 465).</summary>
    SslOnConnect,

    /// <summary>Conexão em texto e upgrade para TLS via STARTTLS (ex.: SMTP 587).</summary>
    StartTls,

    /// <summary>Sem criptografia. Só para servidores locais/de teste.</summary>
    None,
}

public enum AuthMethod
{
    /// <summary>Usuário e senha (ou senha de app).</summary>
    Password,

    /// <summary>OAuth2 via XOAUTH2.</summary>
    OAuth2,
}

public sealed record ServerEndpoint(string Host, int Port, SecurityMode Security);

public sealed record ServerSettings(ServerEndpoint Imap, ServerEndpoint Smtp);

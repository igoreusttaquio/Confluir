namespace Confluir.Core.Models;

/// <summary>Conta configurada. Não contém segredos: a senha fica no <see cref="Services.ICredentialStore"/>.</summary>
public sealed record MailAccount(
    Guid Id,
    string DisplayName,
    string Email,
    MailProvider Provider,
    string ColorHex,
    string UserName,
    ServerSettings Servers,
    AuthMethod Auth = AuthMethod.Password,
    string? OAuthAccountId = null,
    string? SenderName = null);

namespace Confluir.Core.Models;

/// <summary>Conta configurada. Não contém segredos: a senha fica no <see cref="Services.ICredentialStore"/>.</summary>
/// <param name="Signature">Assinatura em HTML (nula = sem assinatura); entra no fim das mensagens novas.</param>
/// <param name="SignatureOnReplies">A assinatura também entra em respostas e encaminhamentos (acima da citação).</param>
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
    string? SenderName = null,
    string? Signature = null,
    bool SignatureOnReplies = true);

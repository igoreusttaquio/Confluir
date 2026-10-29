using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <param name="AccountId">Identificador da conta no provedor de identidade (para renovar o token depois).</param>
/// <param name="Email">Endereço com que o usuário entrou.</param>
public sealed record OAuthSignIn(string AccountId, string Email, string AccessToken);

/// <summary>
/// Login OAuth2 para IMAP/SMTP via XOAUTH2: Microsoft (Outlook/Hotmail/Microsoft 365) e Google (Gmail/Workspace).
/// O provedor de identidade sai do <see cref="MailProvider"/> (no login) ou da própria conta (ao renovar).
/// </summary>
public interface IOAuthTokenProvider
{
    /// <summary>Falso quando o app não tem as credenciais de aplicativo desse provedor (ou ele não usa OAuth).</summary>
    bool IsConfigured(MailProvider provider);

    /// <summary>Abre o navegador do sistema para o usuário entrar. Cancelado pelo usuário → <see cref="OperationCanceledException"/>.</summary>
    Task<OAuthSignIn> SignInAsync(MailProvider provider, string? loginHint, CancellationToken ct = default);

    /// <summary>Token válido para a conta, renovado silenciosamente. Lança <see cref="ReauthenticationRequiredException"/> se for preciso entrar de novo.</summary>
    Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default);

    /// <summary>Remove a sessão da conta do cache de tokens.</summary>
    Task SignOutAsync(MailAccount account, CancellationToken ct = default);
}

/// <summary>Login OAuth de um provedor de identidade só (o <see cref="IOAuthTokenProvider"/> escolhe qual usar).</summary>
public interface IOAuthIdentityProvider
{
    /// <summary>Provedores de email atendidos por este login.</summary>
    bool Handles(MailProvider provider);

    bool IsConfigured { get; }

    Task<OAuthSignIn> SignInAsync(string? loginHint, CancellationToken ct = default);

    Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default);

    Task SignOutAsync(MailAccount account, CancellationToken ct = default);
}

/// <summary>A sessão OAuth expirou ou foi revogada: o usuário precisa entrar de novo.</summary>
public sealed class ReauthenticationRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>O login OAuth falhou (configuração, rede, permissão negada…).</summary>
public sealed class OAuthSignInException(string message, Exception? inner = null) : Exception(message, inner);

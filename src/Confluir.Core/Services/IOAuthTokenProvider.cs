using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <param name="AccountId">Identificador da conta no provedor de identidade (para renovar o token depois).</param>
/// <param name="Email">Endereço com que o usuário entrou.</param>
public sealed record OAuthSignIn(string AccountId, string Email, string AccessToken);

/// <summary>Login OAuth2 (Microsoft) para IMAP/SMTP via XOAUTH2.</summary>
public interface IOAuthTokenProvider
{
    /// <summary>Falso quando o app não tem um ID de aplicativo configurado.</summary>
    bool IsConfigured { get; }

    /// <summary>Abre o navegador do sistema para o usuário entrar. Cancelado pelo usuário → <see cref="OperationCanceledException"/>.</summary>
    Task<OAuthSignIn> SignInAsync(string? loginHint, CancellationToken ct = default);

    /// <summary>Token válido para a conta, renovado silenciosamente. Lança <see cref="ReauthenticationRequiredException"/> se for preciso entrar de novo.</summary>
    Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default);

    /// <summary>Remove a sessão da conta do cache de tokens.</summary>
    Task SignOutAsync(MailAccount account, CancellationToken ct = default);
}

/// <summary>A sessão OAuth expirou ou foi revogada: o usuário precisa entrar de novo.</summary>
public sealed class ReauthenticationRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>O login OAuth falhou (configuração, rede, permissão negada…).</summary>
public sealed class OAuthSignInException(string message, Exception? inner = null) : Exception(message, inner);

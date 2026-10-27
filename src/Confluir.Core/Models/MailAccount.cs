namespace Confluir.Core.Models;

public sealed record MailAccount(
    Guid Id,
    string DisplayName,
    string Email,
    MailProvider Provider,
    string ColorHex);

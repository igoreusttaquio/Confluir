namespace Confluir.Core.Models;

public enum SpecialFolderKind
{
    None,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
}

public sealed record MailFolder(
    Guid Id,
    Guid AccountId,
    string Name,
    string FullName,
    SpecialFolderKind Kind,
    int UnreadCount);

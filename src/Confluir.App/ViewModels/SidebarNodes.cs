using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.Core.Models;
using Lucide.Avalonia;

namespace Confluir.App.ViewModels;

/// <summary>Linha da barra lateral. A lista é achatada para permitir recolher contas sem TreeView.</summary>
public abstract partial class SidebarNode : ViewModelBase
{
    public virtual bool IsSelectable => true;
}

public sealed partial class UnifiedInboxNode : SidebarNode
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    public string Title => Strings.UnifiedInbox;
}

public sealed partial class AccountNode(MailAccount account, IReadOnlyList<FolderNode> folders, Action<AccountNode> toggled)
    : SidebarNode
{
    public MailAccount Account { get; } = account;
    public IReadOnlyList<FolderNode> Folders { get; } = folders;
    public override bool IsSelectable => false;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        toggled(this);
    }
}

public sealed partial class FolderNode(MailFolder folder, MailAccount account) : SidebarNode
{
    public MailFolder Folder { get; } = folder;
    public MailAccount Account { get; } = account;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; } = folder.UnreadCount;

    public bool HasUnread => UnreadCount > 0;

    public string Title => Folder.Kind switch
    {
        SpecialFolderKind.Inbox => Strings.Folder_Inbox,
        SpecialFolderKind.Sent => Strings.Folder_Sent,
        SpecialFolderKind.Drafts => Strings.Folder_Drafts,
        SpecialFolderKind.Trash => Strings.Folder_Trash,
        SpecialFolderKind.Junk => Strings.Folder_Junk,
        SpecialFolderKind.Archive => Strings.Folder_Archive,
        _ => Folder.Name,
    };

    public LucideIconKind Icon => Folder.Kind switch
    {
        SpecialFolderKind.Inbox => LucideIconKind.Inbox,
        SpecialFolderKind.Sent => LucideIconKind.Send,
        SpecialFolderKind.Drafts => LucideIconKind.FilePen,
        SpecialFolderKind.Trash => LucideIconKind.Trash,
        SpecialFolderKind.Junk => LucideIconKind.ShieldAlert,
        SpecialFolderKind.Archive => LucideIconKind.Archive,
        _ => LucideIconKind.Folder,
    };
}

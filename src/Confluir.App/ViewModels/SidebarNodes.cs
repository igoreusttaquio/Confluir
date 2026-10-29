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
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(RailCount))]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Contador da barra recolhida, curto para caber no canto do ícone.</summary>
    public string RailCount => UnreadCount > 99 ? "99+" : UnreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string Title => Strings.UnifiedInbox;
}

public interface IAccountNodeHost
{
    void OnToggled(AccountNode node);
    void RetryAccount(AccountNode node);
    Task RemoveAccountAsync(AccountNode node);
    void EditAccount(AccountNode node);

    /// <summary>Cria a pasta; devolve o erro para mostrar (nulo = criada).</summary>
    Task<string?> CreateFolderAsync(AccountNode node, string name);

    void EditSignature(AccountNode node);
}

public sealed partial class AccountNode(MailAccount account, IAccountNodeHost host) : SidebarNode
{
    public MailAccount Account { get; } = account;
    public override bool IsSelectable => false;

    /// <summary>Inicial do nome da conta, para a barra lateral recolhida.</summary>
    public string Initial => string.IsNullOrWhiteSpace(Account.DisplayName)
        ? "?"
        : Account.DisplayName.Trim()[..1].ToUpperInvariant();

    public IReadOnlyList<FolderNode> Folders { get; set; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>Sincronizando com o servidor.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Sem conexão: exibindo o que está em cache.</summary>
    [ObservableProperty]
    public partial bool IsOffline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Faixa de confirmação "Remover conta?" sob o cabeçalho (sem diálogo modal).</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemoval { get; set; }

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        host.OnToggled(this);
    }

    [RelayCommand]
    private void Retry() => host.RetryAccount(this);

    [RelayCommand]
    private void Edit() => host.EditAccount(this);

    [RelayCommand]
    private void EditSignature() => host.EditSignature(this);

    [RelayCommand]
    private void AskRemove() => IsConfirmingRemoval = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemoval = false;

    [RelayCommand]
    private Task ConfirmRemove() => host.RemoveAccountAsync(this);
    // Nova pasta: campo sob o cabeçalho da conta (sem diálogo).

    [ObservableProperty]
    public partial bool IsCreatingFolder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCreateFolderCommand))]
    public partial string NewFolderName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderError))]
    public partial string? FolderError { get; set; }

    public bool HasFolderError => FolderError is not null;

    [RelayCommand]
    private void AskCreateFolder()
    {
        NewFolderName = "";
        FolderError = null;
        if (!IsExpanded)
            Toggle();
        IsCreatingFolder = true;
    }

    [RelayCommand]
    private void CancelCreateFolder()
    {
        IsCreatingFolder = false;
        FolderError = null;
    }

    private bool CanConfirmCreateFolder() => !string.IsNullOrWhiteSpace(NewFolderName);

    [RelayCommand(CanExecute = nameof(CanConfirmCreateFolder))]
    private async Task ConfirmCreateFolderAsync()
    {
        FolderError = null;
        var error = await host.CreateFolderAsync(this, NewFolderName.Trim());
        if (error is null)
            IsCreatingFolder = false;
        else
            FolderError = error;
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

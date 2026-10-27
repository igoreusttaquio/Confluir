using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IAccountNodeHost
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IMailboxService _mailbox;
    private readonly IAccountStore _accountStore;
    private readonly ICredentialStore _credentials;
    private readonly Func<AddAccountViewModel> _addAccountPage;
    private readonly TimeProvider _time;
    private readonly ILogger<MainWindowViewModel> _logger;

    private readonly UnifiedInboxNode _unified = new();
    private readonly Dictionary<Guid, MailAccount> _accounts = [];
    private readonly Dictionary<Guid, FolderNode> _folderNodes = [];

    // Mensagens marcadas como lidas nesta sessão, para manter o estado ao trocar de pasta.
    private readonly HashSet<Guid> _locallyRead = [];

    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailCts;

    public MainWindowViewModel(
        IMailboxService mailbox,
        IAccountStore accountStore,
        ICredentialStore credentials,
        NavigationService navigation,
        Func<AddAccountViewModel> addAccountPage,
        AppearanceViewModel appearance,
        TimeProvider time,
        ILogger<MainWindowViewModel> logger)
    {
        _mailbox = mailbox;
        _accountStore = accountStore;
        _credentials = credentials;
        _addAccountPage = addAccountPage;
        _time = time;
        _logger = logger;
        Navigation = navigation;
        Appearance = appearance;

        _accountStore.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = ReloadAsync());
    }

    public AppearanceViewModel Appearance { get; }

    public NavigationService Navigation { get; }

    public ObservableCollection<SidebarNode> SidebarNodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoAccounts))]
    public partial bool HasAccounts { get; private set; } = true;

    public bool ShowNoAccounts => !HasAccounts;

    [ObservableProperty]
    public partial SidebarNode? SelectedNode { get; set; }

    [ObservableProperty]
    public partial string ListTitle { get; private set; } = Strings.UnifiedInbox;

    [ObservableProperty]
    public partial string ListSubtitle { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEmpty))]
    public partial IReadOnlyList<MessageListRow> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEmpty))]
    public partial bool IsListLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListError), nameof(IsListEmpty))]
    public partial string? ListError { get; private set; }

    public bool HasListError => ListError is not null;

    public bool IsListEmpty => Rows.Count == 0 && !IsListLoading && !HasListError && HasAccounts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial MessageListRow? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial MessageDetailViewModel? Detail { get; private set; }

    public bool HasSelection => Detail is not null;

    public Task InitializeAsync() => ReloadAsync();

    /// <summary>Recarrega contas e pastas (ao iniciar e quando contas são adicionadas ou removidas).</summary>
    public async Task ReloadAsync()
    {
        IReadOnlyList<MailAccount> accounts;
        try
        {
            accounts = await _mailbox.GetAccountsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar contas");
            return;
        }

        SidebarNodes.Clear();
        _accounts.Clear();
        _folderNodes.Clear();
        SelectedRow = null;

        HasAccounts = accounts.Count > 0;
        SidebarNodes.Add(_unified);
        var nodes = new List<AccountNode>();
        foreach (var account in accounts)
        {
            _accounts[account.Id] = account;
            var node = new AccountNode(account, this);
            nodes.Add(node);
            SidebarNodes.Add(node);
        }

        RecountUnified();
        SelectedNode = null;
        SelectedNode = _unified;
        await Task.WhenAll(nodes.Select(ReloadFoldersAsync));
    }

    public async Task ReloadFoldersAsync(AccountNode node)
    {
        node.IsLoading = true;
        node.Error = null;
        try
        {
            var folders = await _mailbox.GetFoldersAsync(node.Account.Id);
            var folderNodes = folders.Select(f => new FolderNode(f, node.Account)).ToList();

            foreach (var old in node.Folders)
            {
                _folderNodes.Remove(old.Folder.Id);
                SidebarNodes.Remove(old);
            }
            node.Folders = folderNodes;
            foreach (var f in folderNodes)
                _folderNodes[f.Folder.Id] = f;
            if (node.IsExpanded)
                InsertFolders(node);
            RecountUnified();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao carregar pastas da conta {AccountId}", node.Account.Id);
            node.Error = ErrorMessages.For(ex, node.Account.Provider);
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    public async Task RemoveAccountAsync(AccountNode node)
    {
        try
        {
            await _credentials.DeleteAsync(CredentialKeys.Password(node.Account.Id));
        }
        catch (Exception ex)
        {
            // A conta sai mesmo assim; o segredo órfão não é usado por mais ninguém.
            _logger.LogWarning(ex, "Falha ao apagar a credencial da conta {AccountId}", node.Account.Id);
        }
        _accountStore.Remove(node.Account.Id);
        _logger.LogInformation("Conta {AccountId} removida", node.Account.Id);
    }

    public void OnToggled(AccountNode account)
    {
        if (account.IsExpanded)
        {
            InsertFolders(account);
            return;
        }
        foreach (var folder in account.Folders)
            SidebarNodes.Remove(folder);
        if (SelectedNode is FolderNode f && account.Folders.Contains(f))
            SelectedNode = null;
    }

    [RelayCommand]
    private void AddAccount() => Navigation.NavigateTo(_addAccountPage());

    [RelayCommand]
    private void GoBack()
    {
        if (!Navigation.IsAtRoot)
            Navigation.GoBack();
    }

    [RelayCommand]
    private Task RetryListAsync() => SelectedNode is { IsSelectable: true } node ? LoadListAsync(node) : Task.CompletedTask;

    partial void OnSelectedNodeChanged(SidebarNode? value)
    {
        if (value is { IsSelectable: true })
            _ = LoadListAsync(value);
    }

    partial void OnSelectedRowChanged(MessageListRow? value)
    {
        _detailCts?.Cancel();
        if (value is not MessageRow row)
        {
            Detail = null;
            return;
        }

        MarkRead(row);
        var detail = new MessageDetailViewModel(row, _mailbox, _logger);
        Detail = detail;
        _detailCts = new CancellationTokenSource();
        _ = detail.LoadAsync(_detailCts.Token);
    }

    private void InsertFolders(AccountNode account)
    {
        var index = SidebarNodes.IndexOf(account);
        if (index < 0)
            return;
        for (var i = 0; i < account.Folders.Count; i++)
            SidebarNodes.Insert(index + 1 + i, account.Folders[i]);
    }

    private async Task LoadListAsync(SidebarNode node)
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        ListError = null;
        IsListLoading = true;
        var provider = MailProvider.Imap;
        try
        {
            IReadOnlyList<MessageSummary> messages;
            switch (node)
            {
                case UnifiedInboxNode u:
                    ListTitle = u.Title;
                    messages = await _mailbox.GetUnifiedInboxAsync(cts.Token);
                    break;
                case FolderNode f:
                    ListTitle = $"{f.Title} · {f.Account.DisplayName}";
                    provider = f.Account.Provider;
                    messages = await _mailbox.GetMessagesAsync(f.Folder.Id, cts.Token);
                    break;
                default:
                    return;
            }

            var now = _time.GetLocalNow();
            var rows = await Task.Run(
                () => MessageListBuilder.Build(messages, _accounts, _locallyRead, now, Culture), cts.Token);
            if (cts.IsCancellationRequested)
                return;

            SelectedRow = null;
            Rows = rows;
            UpdateSubtitle(node);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar a lista de mensagens");
            if (cts.IsCancellationRequested)
                return;
            Rows = [];
            ListSubtitle = "";
            ListError = ErrorMessages.For(ex, provider);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsListLoading = false;
        }
    }

    private void MarkRead(MessageRow row)
    {
        if (row.IsRead)
            return;
        SetRead(row, true);
        _ = PersistReadAsync(row);
    }

    private async Task PersistReadAsync(MessageRow row)
    {
        try
        {
            await _mailbox.MarkAsReadAsync(row.Summary.Id);
        }
        catch (Exception ex)
        {
            // UI otimista: se o servidor recusar, desfaz localmente.
            _logger.LogWarning(ex, "Falha ao marcar mensagem como lida");
            SetRead(row, false);
        }
    }

    private void SetRead(MessageRow row, bool read)
    {
        row.IsRead = read;
        if (read)
            _locallyRead.Add(row.Summary.Id);
        else
            _locallyRead.Remove(row.Summary.Id);

        if (_folderNodes.TryGetValue(row.Summary.FolderId, out var folder))
            folder.UnreadCount = Math.Max(0, folder.UnreadCount + (read ? -1 : 1));
        RecountUnified();
        if (SelectedNode is { } node)
            UpdateSubtitle(node);
    }

    private void UpdateSubtitle(SidebarNode node)
    {
        if (!HasAccounts)
        {
            ListSubtitle = "";
            return;
        }
        var total = Rows.Count(r => r is MessageRow);
        var unread = node switch
        {
            UnifiedInboxNode u => u.UnreadCount,
            FolderNode f => f.UnreadCount,
            _ => 0,
        };
        ListSubtitle = unread > 0
            ? $"{string.Format(Culture, Strings.MessageList_Count, total)} · {string.Format(Culture, Strings.MessageList_UnreadCount, unread)}"
            : string.Format(Culture, Strings.MessageList_Count, total);
    }

    private void RecountUnified() =>
        _unified.UnreadCount = _folderNodes.Values
            .Where(f => f.Folder.Kind == SpecialFolderKind.Inbox)
            .Sum(f => f.UnreadCount);
}

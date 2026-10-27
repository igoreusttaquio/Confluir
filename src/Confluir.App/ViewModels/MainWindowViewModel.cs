using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Confluir.App.Resources;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IMailboxService _mailbox;
    private readonly TimeProvider _time;
    private readonly ILogger<MainWindowViewModel> _logger;

    private readonly UnifiedInboxNode _unified = new();
    private readonly Dictionary<Guid, MailAccount> _accounts = [];
    private readonly Dictionary<Guid, FolderNode> _folderNodes = [];

    // Mensagens marcadas como lidas nesta sessão (a sincronização real chega na fase 3).
    private readonly HashSet<Guid> _locallyRead = [];

    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailCts;

    public MainWindowViewModel(
        IMailboxService mailbox,
        AppearanceViewModel appearance,
        TimeProvider time,
        ILogger<MainWindowViewModel> logger)
    {
        _mailbox = mailbox;
        _time = time;
        _logger = logger;
        Appearance = appearance;
    }

    public AppearanceViewModel Appearance { get; }

    public ObservableCollection<SidebarNode> SidebarNodes { get; } = [];

    [ObservableProperty]
    public partial SidebarNode? SelectedNode { get; set; }

    [ObservableProperty]
    public partial string ListTitle { get; private set; } = Strings.UnifiedInbox;

    [ObservableProperty]
    public partial string ListSubtitle { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEmpty))]
    public partial IReadOnlyList<MessageListRow> Rows { get; private set; } = [];

    public bool IsListEmpty => Rows.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial MessageListRow? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial MessageDetailViewModel? Detail { get; private set; }

    public bool HasSelection => Detail is not null;

    public async Task InitializeAsync()
    {
        var accounts = await _mailbox.GetAccountsAsync();
        SidebarNodes.Add(_unified);

        foreach (var account in accounts)
        {
            _accounts[account.Id] = account;
            var folders = await _mailbox.GetFoldersAsync(account.Id);
            var nodes = folders.Select(f => new FolderNode(f, account)).ToList();
            foreach (var node in nodes)
                _folderNodes[node.Folder.Id] = node;

            var accountNode = new AccountNode(account, nodes, OnAccountToggled);
            SidebarNodes.Add(accountNode);
            foreach (var node in nodes)
                SidebarNodes.Add(node);
        }

        RecountUnified();
        SelectedNode = _unified;
    }

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
        var detail = new MessageDetailViewModel(row);
        Detail = detail;
        _detailCts = new CancellationTokenSource();
        _ = detail.LoadAsync(_mailbox, _logger, _detailCts.Token);
    }

    private async Task LoadListAsync(SidebarNode node)
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar a lista de mensagens");
        }
    }

    private void MarkRead(MessageRow row)
    {
        if (row.IsRead)
            return;
        row.IsRead = true;
        _locallyRead.Add(row.Summary.Id);
        if (_folderNodes.TryGetValue(row.Summary.FolderId, out var folder))
            folder.UnreadCount = Math.Max(0, folder.UnreadCount - 1);
        RecountUnified();
        if (SelectedNode is { } node)
            UpdateSubtitle(node);
    }

    private void UpdateSubtitle(SidebarNode node)
    {
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

    private void OnAccountToggled(AccountNode account)
    {
        var index = SidebarNodes.IndexOf(account);
        if (account.IsExpanded)
        {
            for (var i = 0; i < account.Folders.Count; i++)
                SidebarNodes.Insert(index + 1 + i, account.Folders[i]);
        }
        else
        {
            foreach (var folder in account.Folders)
                SidebarNodes.Remove(folder);
            if (SelectedNode is FolderNode f && account.Folders.Contains(f))
                SelectedNode = null;
        }
    }
}

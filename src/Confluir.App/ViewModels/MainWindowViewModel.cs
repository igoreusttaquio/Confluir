using System.Collections.ObjectModel;
using Avalonia.Threading;
using Confluir.App.Localization;
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
    private readonly LocalizationService _loc;
    private readonly ReadingServices _reading;
    private readonly IMailboxService _mailbox;
    private readonly IAccountStore _accountStore;
    private readonly AccountService _accountService;
    private readonly Func<AccountEditorViewModel> _accountEditor;
    private readonly TimeProvider _time;
    private readonly ILogger<MainWindowViewModel> _logger;

    private readonly UnifiedInboxNode _unified = new();
    private readonly Dictionary<Guid, MailAccount> _accounts = [];
    private readonly Dictionary<Guid, FolderNode> _folderNodes = [];

    // Mensagens marcadas como lidas nesta sessão, para manter o estado ao trocar de pasta.
    private readonly HashSet<Guid> _locallyRead = [];

    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailCts;

    // Mensagens da lista atual, para reconstruir as linhas (ex.: troca de idioma) sem ir ao servidor.
    private IReadOnlyList<MessageSummary> _listMessages = [];
    private bool _suppressSelection;

    public MainWindowViewModel(
        IMailboxService mailbox,
        IAccountStore accountStore,
        AccountService accountService,
        NavigationService navigation,
        Func<AccountEditorViewModel> accountEditor,
        AppearanceViewModel appearance,
        LocalizationService localization,
        ReadingServices reading,
        TimeProvider time,
        ILogger<MainWindowViewModel> logger)
    {
        _reading = reading;
        // O HTML do email usa as cores do tema: remonta ao trocar de tema ou de cor de destaque.
        reading.Theme.PaletteChanged += (_, _) => Dispatcher.UIThread.Post(() => Detail?.RebuildDocument());
        _loc = localization;
        _loc.LanguageChanged += (_, _) => OnLanguageChanged();
        _mailbox = mailbox;
        _accountStore = accountStore;
        _accountService = accountService;
        _accountEditor = accountEditor;
        _time = time;
        _logger = logger;
        Navigation = navigation;
        Appearance = appearance;

        _accountStore.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = ReloadAsync());

        // A sincronização roda em segundo plano; tudo que mexe na interface volta para a thread de UI.
        _mailbox.MailboxChanged += (_, e) => Dispatcher.UIThread.Post(() => ScheduleRefresh(e.AccountId));
        _mailbox.AccountStatusChanged += (_, e) => Dispatcher.UIThread.Post(() => OnAccountStatus(e));
        _mailbox.OperationFailed += (_, e) => Dispatcher.UIThread.Post(() => OnOperationFailed(e));
    }

    // Atualização a partir da sincronização ---------------------------------------------------------

    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(700);
    private readonly HashSet<Guid> _pendingRefresh = [];
    private bool _refreshScheduled;

    /// <summary>Junta avisos seguidos (lotes da sincronização inicial) numa única atualização da tela.</summary>
    private void ScheduleRefresh(Guid accountId)
    {
        _pendingRefresh.Add(accountId);
        if (_refreshScheduled)
            return;
        _refreshScheduled = true;
        DispatcherTimer.RunOnce(() => _ = RefreshAsync(), RefreshDelay);
    }

    private async Task RefreshAsync()
    {
        _refreshScheduled = false;
        var accounts = _pendingRefresh.ToList();
        _pendingRefresh.Clear();

        foreach (var node in SidebarNodes.OfType<AccountNode>().Where(n => accounts.Contains(n.Account.Id)).ToList())
            await ReloadFoldersAsync(node);

        var affectsList = SelectedNode switch
        {
            UnifiedInboxNode => true,
            FolderNode f => accounts.Contains(f.Account.Id),
            _ => false,
        };
        if (affectsList && SelectedNode is { } selected)
            await LoadListAsync(selected, keepSelection: true);
    }

    private void OnAccountStatus(AccountStatusEventArgs e)
    {
        if (SidebarNodes.OfType<AccountNode>().FirstOrDefault(n => n.Account.Id == e.AccountId) is { } node)
            ApplyStatus(node, e.Status, e.Error);
    }

    private static void ApplyStatus(AccountNode node, SyncStatus status, Exception? error)
    {
        node.IsLoading = status == SyncStatus.Syncing;
        node.IsOffline = status == SyncStatus.Offline;
        node.Error = status == SyncStatus.Error && error is not null
            ? ErrorMessages.For(error, node.Account.Provider)
            : null;
    }

    private void OnOperationFailed(OperationFailedEventArgs e)
    {
        // O cache já foi revertido; desfaz também a linha exibida.
        if (Rows.OfType<MessageRow>().FirstOrDefault(r => r.Summary.Id == e.MessageId) is { IsRead: true } row &&
            e.Kind == OperationKind.MarkRead)
            SetRead(row, false);
    }

    [RelayCommand]
    private void Refresh() => _mailbox.RequestSync();

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
            ApplyStatus(node, _mailbox.GetStatus(account.Id), null);
            nodes.Add(node);
            SidebarNodes.Add(node);
        }

        RecountUnified();
        SelectedNode = null;
        SelectedNode = _unified;
        await Task.WhenAll(nodes.Select(ReloadFoldersAsync));
    }

    /// <summary>
    /// Atualiza as pastas da conta a partir do cache. Reaproveita os nós existentes (mesmo id), para que a
    /// pasta selecionada continue selecionada enquanto a sincronização atualiza contadores.
    /// </summary>
    public async Task ReloadFoldersAsync(AccountNode node)
    {
        IReadOnlyList<MailFolder> folders;
        try
        {
            folders = await _mailbox.GetFoldersAsync(node.Account.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler as pastas da conta {AccountId} do cache", node.Account.Id);
            return;
        }

        var existing = node.Folders.ToDictionary(f => f.Folder.Id);
        var updated = folders.Select(f =>
        {
            if (existing.TryGetValue(f.Id, out var current) && current.Folder with { UnreadCount = 0 } == f with { UnreadCount = 0 })
            {
                current.UnreadCount = f.UnreadCount;
                return current;
            }
            return new FolderNode(f, node.Account);
        }).ToList();

        if (!updated.SequenceEqual(node.Folders))
        {
            foreach (var old in node.Folders)
            {
                _folderNodes.Remove(old.Folder.Id);
                SidebarNodes.Remove(old);
            }
            node.Folders = updated;
            foreach (var f in updated)
                _folderNodes[f.Folder.Id] = f;
            if (node.IsExpanded)
                InsertFolders(node);
            if (SelectedNode is FolderNode selected && !_folderNodes.ContainsKey(selected.Folder.Id))
                SelectedNode = _unified; // a pasta selecionada sumiu do servidor
        }
        RecountUnified();
        if (SelectedNode is { } selectedNode)
            UpdateSubtitle(selectedNode);
    }

    public void RetryAccount(AccountNode node) => _mailbox.RequestSync(node.Account.Id);

    public Task RemoveAccountAsync(AccountNode node) => _accountService.RemoveAsync(node.Account);

    public void EditAccount(AccountNode node)
    {
        var editor = _accountEditor();
        editor.Load(node.Account);
        Navigation.NavigateTo(editor);
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
    private void AddAccount() => Navigation.NavigateTo(_accountEditor());

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
        if (value is not { IsSelectable: true })
            return;
        // Escolher uma pasta na barra lateral sempre leva de volta à caixa de email.
        Navigation.GoToRoot();
        _ = LoadListAsync(value);
    }

    partial void OnSelectedRowChanged(MessageListRow? value)
    {
        if (_suppressSelection)
            return;
        _detailCts?.Cancel();
        if (value is not MessageRow row)
        {
            Detail = null;
            return;
        }

        MarkRead(row);
        var detail = new MessageDetailViewModel(row, _reading);
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

    /// <param name="keepSelection">
    /// Atualização vinda da sincronização: sem indicador de carregamento e mantendo a mensagem aberta.
    /// </param>
    private async Task LoadListAsync(SidebarNode node, bool keepSelection = false)
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        ListError = null;
        IsListLoading = !keepSelection;
        var provider = MailProvider.Imap;
        try
        {
            IReadOnlyList<MessageSummary> messages;
            UpdateListTitle(node);
            switch (node)
            {
                case UnifiedInboxNode:
                    messages = await _mailbox.GetUnifiedInboxAsync(cts.Token);
                    break;
                case FolderNode f:
                    provider = f.Account.Provider;
                    messages = await _mailbox.GetMessagesAsync(f.Folder.Id, cts.Token);
                    break;
                default:
                    return;
            }

            var rows = await Task.Run(() => BuildRows(messages), cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _listMessages = messages;
            if (keepSelection)
            {
                ReplaceRowsKeepingSelection(rows);
            }
            else
            {
                SelectedRow = null;
                Rows = rows;
            }
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
        var count = _loc.Plural("MessageList_Count", total);
        ListSubtitle = unread > 0
            ? $"{count} · {_loc.Plural("MessageList_UnreadCount", unread)}"
            : count;
    }

    /// <summary>
    /// Troca as linhas mantendo a mensagem aberta, sem disparar nova leitura. A supressão cobre também a troca
    /// de Rows: o ListBox limpa a seleção ao receber novos itens. Se a mensagem sumiu, fecha o painel.
    /// </summary>
    private void ReplaceRowsKeepingSelection(List<MessageListRow> rows)
    {
        var selectedId = (SelectedRow as MessageRow)?.Summary.Id;
        _suppressSelection = true;
        try
        {
            Rows = rows;
            SelectedRow = selectedId is { } id
                ? rows.OfType<MessageRow>().FirstOrDefault(r => r.Summary.Id == id)
                : null;
        }
        finally
        {
            _suppressSelection = false;
        }
        if (SelectedRow is null)
            Detail = null;
    }

    private void UpdateListTitle(SidebarNode node) => ListTitle = node switch
    {
        UnifiedInboxNode u => u.Title,
        FolderNode f => $"{f.Title} · {f.Account.DisplayName}",
        _ => ListTitle,
    };

    private List<MessageListRow> BuildRows(IReadOnlyList<MessageSummary> messages) =>
        MessageListBuilder.Build(messages, _accounts, _locallyRead, _time.GetLocalNow(), _loc.FormatCulture);

    /// <summary>Recalcula os textos derivados no novo idioma, sem buscar nada no servidor.</summary>
    public override void OnLanguageChanged()
    {
        foreach (var node in SidebarNodes)
            node.OnLanguageChanged();

        ReplaceRowsKeepingSelection(BuildRows(_listMessages));
        if (SelectedNode is { } selected)
        {
            UpdateListTitle(selected);
            UpdateSubtitle(selected);
        }
        Detail?.OnLanguageChanged();
        Navigation.CurrentPage?.OnLanguageChanged();
        Appearance.OnLanguageChanged();
        base.OnLanguageChanged();
    }

    private void RecountUnified() =>
        _unified.UnreadCount = _folderNodes.Values
            .Where(f => f.Folder.Kind == SpecialFolderKind.Inbox)
            .Sum(f => f.UnreadCount);
}

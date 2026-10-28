using System.Collections.ObjectModel;
using Avalonia.Threading;
using Confluir.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Compose;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IAccountNodeHost, IMessageActions
{
    private readonly LocalizationService _loc;
    private readonly ReadingServices _reading;
    private readonly ComposeServices _compose;
    private readonly SettingsServices _settingsServices;
    private DispatcherTimer? _markReadTimer;
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
        ComposeServices compose,
        SettingsServices settingsServices,
        TimeProvider time,
        ILogger<MainWindowViewModel> logger)
    {
        _reading = reading;
        _compose = compose;
        _settingsServices = settingsServices;
        // Fontes, escala e altura de linha também entram no HTML do email.
        settingsServices.Fonts.Changed += (_, _) => Dispatcher.UIThread.Post(() => Detail?.RebuildDocument());
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
        appearance.LayoutChanged += (_, _) => OnLayoutChanged();
        SidebarNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AccountNodes));

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
        // O cache já foi revertido; desfaz também a linha exibida (arquivar/excluir voltam pela sincronização).
        if (Rows.OfType<MessageRow>().FirstOrDefault(r => r.Summary.Id == e.MessageId) is not { } row)
            return;
        switch (e.Kind)
        {
            case OperationKind.MarkRead when row.IsRead:
                SetRead(row, false);
                break;
            case OperationKind.MarkUnread when !row.IsRead:
                SetRead(row, true);
                break;
            case OperationKind.Flag or OperationKind.Unflag:
                row.IsFlagged = e.Kind == OperationKind.Unflag;
                break;
        }
    }

    // Ações sobre mensagens ---------------------------------------------------------------------------

    public bool CanArchive(MessageDetailViewModel message) =>
        _folderNodes.Values.Any(f => f.Account.Id == message.Row.Account.Id && f.Folder.Kind == SpecialFolderKind.Archive) &&
        !(_folderNodes.TryGetValue(message.Row.Summary.FolderId, out var current) && current.Folder.Kind == SpecialFolderKind.Archive);

    public Task ArchiveAsync(MessageDetailViewModel message) => RemoveFromListAsync(message, _mailbox.ArchiveAsync);

    public Task DeleteAsync(MessageDetailViewModel message) => RemoveFromListAsync(message, _mailbox.DeleteAsync);

    // Composição --------------------------------------------------------------------------------------

    /// <summary>Nova mensagem, saindo pela conta da pasta aberta (ou a primeira).</summary>
    [RelayCommand]
    private void NewMessage()
    {
        var accountId = (SelectedNode as FolderNode)?.Account.Id;
        Navigation.NavigateTo(new ComposeViewModel(_compose, _accounts.Values.ToList(), accountId, ShowStatus));
    }

    public async Task ComposeAsync(MessageDetailViewModel message, ComposeMode mode)
    {
        var body = message.LoadedBody;
        try
        {
            body ??= await _mailbox.GetMessageBodyAsync(message.Row.Summary.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível carregar a mensagem para responder");
            ShowStatus(Strings.Reading_NotAvailableOffline);
            return;
        }
        var compose = new ComposeViewModel(_compose, _accounts.Values.ToList(), message.Row.Account.Id, ShowStatus);
        compose.InitFrom(mode, message.Row.Summary, body, message.Row.Account);
        Navigation.NavigateTo(compose);
    }

    /// <summary>
    /// Continua um rascunho da pasta Rascunhos: a versão do aparelho se for um rascunho do Confluir
    /// (pode estar mais nova que a do servidor), senão o conteúdo do servidor.
    /// </summary>
    public async Task EditDraftAsync(MessageDetailViewModel message)
    {
        var summary = message.Row.Summary;
        var body = message.LoadedBody;
        try
        {
            body ??= await _mailbox.GetMessageBodyAsync(summary.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível carregar o rascunho");
            ShowStatus(Strings.Reading_NotAvailableOffline);
            return;
        }
        var draftId = DraftMessageId.Parse(body.MessageIdHeader);
        var location = await _compose.Drafts.LocateAsync(summary.Id);
        var local = draftId is { } id ? await _compose.Drafts.GetAsync(id) : null;

        var compose = new ComposeViewModel(_compose, _accounts.Values.ToList(), summary.AccountId, ShowStatus, draftId);
        if (local is not null)
            compose.InitFromDraft(local with { Server = location ?? local.Server });
        else
            compose.InitFromServerDraft(summary, body, location);
        if (Navigation.CurrentPage is MessagePageViewModel)
            Navigation.GoBack();
        Navigation.NavigateTo(compose);
    }

    private bool IsDraftFolder(Guid folderId) =>
        _folderNodes.TryGetValue(folderId, out var folder) && folder.Folder.Kind == SpecialFolderKind.Drafts;

    // Aviso temporário (ex.: "Mensagem enviada") -------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusMessage { get; private set; }

    public bool HasStatus => StatusMessage is not null;

    private int _statusId;

    public void ShowStatus(string message)
    {
        StatusMessage = message;
        var id = ++_statusId;
        DispatcherTimer.RunOnce(() =>
        {
            if (id == _statusId)
                StatusMessage = null;
        }, TimeSpan.FromSeconds(Math.Clamp(message.Length / 12.0, 4, 12)));
    }

    public async Task ToggleFlagAsync(MessageDetailViewModel message)
    {
        var row = message.Row;
        row.IsFlagged = !row.IsFlagged;
        await _mailbox.SetFlaggedAsync(row.Summary.Id, row.IsFlagged);
    }

    public async Task ToggleReadAsync(MessageDetailViewModel message)
    {
        var row = message.Row;
        SetRead(row, !row.IsRead);
        await _mailbox.SetReadAsync(row.Summary.Id, row.IsRead);
    }

    /// <summary>
    /// Arquivar/excluir: tira a mensagem da lista na hora, ajusta contadores, fecha a tela própria (se aberta)
    /// e seleciona a mensagem seguinte.
    /// </summary>
    private async Task RemoveFromListAsync(MessageDetailViewModel message, Func<Guid, CancellationToken, Task> action)
    {
        var row = message.Row;
        var messages = Rows.OfType<MessageRow>().ToList();
        var index = messages.FindIndex(r => r.Summary.Id == row.Summary.Id);
        var next = index >= 0 && index + 1 < messages.Count ? messages[index + 1]
                 : index > 0 ? messages[index - 1]
                 : null;

        await action(row.Summary.Id, CancellationToken.None);

        if (!row.IsRead && _folderNodes.TryGetValue(row.Summary.FolderId, out var folder))
            folder.UnreadCount = Math.Max(0, folder.UnreadCount - 1);
        RecountUnified();

        if (Navigation.CurrentPage is MessagePageViewModel page && page.Detail == message)
            Navigation.GoBack();

        _listMessages = _listMessages.Where(m => m.Id != row.Summary.Id).ToList();
        var rows = BuildRows(_listMessages);
        _suppressSelection = true;
        try
        {
            Rows = rows;
            SelectedRow = null;
        }
        finally
        {
            _suppressSelection = false;
        }
        Detail = null;
        if (next is not null)
            SelectedRow = rows.OfType<MessageRow>().FirstOrDefault(r => r.Summary.Id == next.Summary.Id);
        if (SelectedNode is { } node)
            UpdateSubtitle(node);
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

    public async Task InitializeAsync()
    {
        await ReloadAsync();
        // Rascunhos que ficaram só no aparelho (ex.: app fechado sem conexão) vão para o servidor.
        _ = _compose.Drafts.UploadPendingAsync(_accounts.Keys.ToList());
    }

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
    private void OpenSettings()
    {
        if (Navigation.CurrentPage is SettingsViewModel)
            return;
        Navigation.NavigateTo(new SettingsViewModel(_settingsServices, this));
    }

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
        // Em duas colunas a seleção só marca a linha; a mensagem abre em tela própria (OpenMessageCommand).
        if (value is not MessageRow row || IsTwoColumnLayout)
        {
            Detail = null;
            return;
        }

        MarkRead(row);
        Detail = CreateDetail(row);
    }

    private MessageDetailViewModel CreateDetail(MessageRow row)
    {
        var detail = new MessageDetailViewModel(row, _reading)
        {
            OpenInPageRequested = OpenInPage,
            Actions = this,
            IsDraft = IsDraftFolder(row.Summary.FolderId),
        };
        _detailCts = new CancellationTokenSource();
        _ = detail.LoadAsync(_detailCts.Token);
        return detail;
    }

    /// <summary>Abre a mensagem selecionada em tela própria (clique em duas colunas, duplo clique ou Enter).</summary>
    [RelayCommand]
    private void OpenMessage()
    {
        if (SelectedRow is not MessageRow row)
            return;
        MarkRead(row);
        // No layout de três colunas reaproveita o que já está carregado no painel de leitura.
        var detail = Detail is { } current && current.Row.Summary.Id == row.Summary.Id ? current : CreateDetail(row);
        // Rascunho abre direto na tela de composição.
        if (detail.IsDraft)
            _ = EditDraftAsync(detail);
        else
            OpenInPage(detail);
    }

    private void OpenInPage(MessageDetailViewModel detail) =>
        Navigation.NavigateTo(new MessagePageViewModel(detail, () => Navigation.GoBack()));

    // Layout ------------------------------------------------------------------------------------------

    public bool IsTwoColumnLayout => _reading.Settings.Current.Layout.ReadingLayout == ReadingLayout.TwoColumns;

    public bool IsSidebarCollapsed
    {
        get => _reading.Settings.Current.Layout.SidebarCollapsed;
        private set
        {
            if (value == IsSidebarCollapsed)
                return;
            _reading.Settings.Current.Layout.SidebarCollapsed = value;
            _reading.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSidebarExpanded));
        }
    }

    public bool IsSidebarExpanded => !IsSidebarCollapsed;

    public UnifiedInboxNode UnifiedInbox => _unified;

    /// <summary>Contas para a barra lateral recolhida (uma bolinha colorida por conta).</summary>
    public IEnumerable<AccountNode> AccountNodes => SidebarNodes.OfType<AccountNode>();

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    /// <summary>Na barra recolhida: abre a caixa de entrada da conta (ou a unificada, sem conta).</summary>
    [RelayCommand]
    private void SelectAccountInbox(AccountNode? account)
    {
        SelectedNode = account?.Folders.FirstOrDefault(f => f.Folder.Kind == SpecialFolderKind.Inbox)
                       ?? (SidebarNode)_unified;
    }

    [RelayCommand]
    private void SelectUnified() => SelectedNode = _unified;

    private void OnLayoutChanged()
    {
        OnPropertyChanged(nameof(IsTwoColumnLayout));
        if (IsTwoColumnLayout)
            Detail = null; // a mensagem passa a abrir em tela própria
        else if (SelectedRow is MessageRow row && Detail is null)
            Detail = CreateDetail(row);
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

/// <summary>Marca como lida ao abrir, depois de alguns segundos ou nunca (Configurações → Leitura).</summary>
    private void MarkRead(MessageRow row)
    {
        _markReadTimer?.Stop();
        if (row.IsRead)
            return;
        var delay = _reading.Settings.Current.Reading.MarkReadDelaySeconds;
        if (delay < 0)
            return;
        if (delay == 0)
        {
            MarkReadNow(row);
            return;
        }
        _markReadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        _markReadTimer.Tick += (_, _) =>
        {
            _markReadTimer?.Stop();
            // Só se a mensagem continua aberta (no painel ou em tela própria).
            var open = Detail?.Row == row ||
                       (Navigation.CurrentPage is MessagePageViewModel page && page.Detail.Row == row);
            if (open)
                MarkReadNow(row);
        };
        _markReadTimer.Start();
    }

    private void MarkReadNow(MessageRow row)
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
            await _mailbox.SetReadAsync(row.Summary.Id, true);
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

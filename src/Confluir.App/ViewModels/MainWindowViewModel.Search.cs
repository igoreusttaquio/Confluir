using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.Core.Models;
using Confluir.Core.Search;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

/// <param name="Account">Nula = todas as contas.</param>
public sealed record SearchAccountOption(MailAccount? Account, string Label);

/// <param name="Days">Últimos N dias; 0 = qualquer data; <see cref="CustomDays"/> = de/até escolhidos.</param>
public sealed record SearchPeriodOption(int Days, string LabelKey)
{
    public const int CustomDays = -1;

    public bool IsCustom => Days == CustomDays;
}

/// <summary>
/// Busca: campo no topo da lista (com operadores como <c>de:</c> e <c>assunto:</c>) e filtro avançado
/// (remetente, destinatário, assunto, anexo, não lidas, período, só nesta pasta). Os resultados ocupam a lista,
/// com os filtros rápidos valendo por cima. Sem resultado local, dá para buscar no servidor.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);
    private const int SearchPageSize = 500;
    private int _searchLimit = SearchPageSize;
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _searchTimer;
    private bool _resettingSearch;

    public IReadOnlyList<SearchPeriodOption> SearchPeriods { get; } =
    [
        new(0, nameof(Strings.Search_Period_Any)),
        new(7, nameof(Strings.Search_Period_Week)),
        new(30, nameof(Strings.Search_Period_Month)),
        new(365, nameof(Strings.Search_Period_Year)),
        new(SearchPeriodOption.CustomDays, nameof(Strings.Search_Period_Custom)),
    ];

    /// <summary>Período escolhido à mão: as datas aparecem.</summary>
    public bool IsCustomPeriod => SearchPeriod?.IsCustom == true;

    /// <summary>De (inclusive, desde o começo do dia). Nulo = sem limite.</summary>
    [ObservableProperty]
    public partial DateTime? SearchFromDate { get; set; }

    /// <summary>Até (inclusive, até o fim do dia). Nulo = sem limite.</summary>
    [ObservableProperty]
    public partial DateTime? SearchToDate { get; set; }

    partial void OnSearchFromDateChanged(DateTime? value) => ScheduleSearch();
    partial void OnSearchToDateChanged(DateTime? value) => ScheduleSearch();

    /// <summary>
    /// Limites da busca no fuso local. No personalizado, as datas podem vir trocadas (até antes de de): valem
    /// como intervalo do mesmo jeito.
    /// </summary>
    public static (DateTimeOffset? Since, DateTimeOffset? Until) PeriodBounds(SearchPeriodOption? period, DateTime? from, DateTime? to,
        DateTimeOffset now)
    {
        if (period is null || period.Days == 0)
            return (null, null);
        if (!period.IsCustom)
            return (now.AddDays(-period.Days), null);
        if (from is { } a && to is { } b && a.Date > b.Date)
            (from, to) = (to, from);
        DateTimeOffset? Local(DateTime day) => new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date));
        return (from is { } f ? Local(f) : null, to is { } t ? Local(t.Date.AddDays(1)) : null);
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsAdvancedSearchOpen { get; set; }

    [ObservableProperty]
    public partial string SearchFrom { get; set; } = "";

    [ObservableProperty]
    public partial string SearchTo { get; set; } = "";

    [ObservableProperty]
    public partial string SearchSubject { get; set; } = "";

    [ObservableProperty]
    public partial bool SearchHasAttachment { get; set; }

    [ObservableProperty]
    public partial bool SearchUnread { get; set; }

    [ObservableProperty]
    public partial SearchPeriodOption? SearchPeriod { get; set; }

    /// <summary>Contas para o filtro avançado ("Todas" + cada conta).</summary>
    public IReadOnlyList<SearchAccountOption> SearchAccounts =>
        [new SearchAccountOption(null, Strings.Rule_AllAccounts), .. _accounts.Values.Select(a => new SearchAccountOption(a, a.DisplayName))];

    [ObservableProperty]
    public partial SearchAccountOption? SearchAccount { get; set; }

    partial void OnSearchAccountChanged(SearchAccountOption? value) => ScheduleSearch();

    /// <summary>Há mais resultados além dos mostrados (a busca devolve em páginas de 500).</summary>
    [ObservableProperty]
    public partial bool CanLoadMoreResults { get; private set; }

    [RelayCommand]
    private Task LoadMoreResults()
    {
        _searchLimit += SearchPageSize;
        return RunSearchAsync(keepSelection: true, keepLimit: true);
    }

    /// <summary>Só na pasta aberta (padrão: todas as contas).</summary>
    [ObservableProperty]
    public partial bool SearchInFolder { get; set; }

    /// <summary>Há uma pasta aberta para restringir a busca (na caixa unificada não).</summary>
    public bool CanSearchInFolder => SelectedNode is FolderNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListEmptyText), nameof(CanSearchServer))]
    public partial bool IsSearchActive { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearchServer))]
    public partial bool IsSearchingServer { get; private set; }

    public bool CanSearchServer => IsSearchActive && CurrentQuery().HasText && !IsSearchingServer;

    /// <summary>Busca efetiva: o texto digitado mais os campos do filtro avançado.</summary>
    private SearchQuery CurrentQuery()
    {
        var typed = SearchQuery.Parse(SearchText);
        static IReadOnlyList<string> With(IReadOnlyList<string> list, string extra) =>
            string.IsNullOrWhiteSpace(extra) ? list : [.. list, extra.Trim()];
        return typed with
        {
            From = With(typed.From, SearchFrom),
            To = With(typed.To, SearchTo),
            Subject = With(typed.Subject, SearchSubject),
            HasAttachment = typed.HasAttachment || SearchHasAttachment,
            Unread = typed.Unread || SearchUnread,
        };
    }

    partial void OnSearchTextChanged(string value) => ScheduleSearch();
    partial void OnSearchFromChanged(string value) => ScheduleSearch();
    partial void OnSearchToChanged(string value) => ScheduleSearch();
    partial void OnSearchSubjectChanged(string value) => ScheduleSearch();
    partial void OnSearchHasAttachmentChanged(bool value) => ScheduleSearch();
    partial void OnSearchUnreadChanged(bool value) => ScheduleSearch();
    partial void OnSearchPeriodChanged(SearchPeriodOption? value)
    {
        OnPropertyChanged(nameof(IsCustomPeriod));
        ScheduleSearch();
    }
    partial void OnSearchInFolderChanged(bool value) => ScheduleSearch();

    /// <summary>Espera uma pausa na digitação; a busca anterior é cancelada.</summary>
    private void ScheduleSearch()
    {
        if (_resettingSearch)
            return;
        _searchTimer ??= CreateSearchTimer();
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private DispatcherTimer CreateSearchTimer()
    {
        var timer = new DispatcherTimer { Interval = SearchDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = RunSearchAsync();
        };
        return timer;
    }

    /// <summary>Enter no campo: busca sem esperar.</summary>
    [RelayCommand]
    private Task SearchNow()
    {
        _searchTimer?.Stop();
        return RunSearchAsync();
    }

    private async Task RunSearchAsync(bool keepSelection = false, bool keepLimit = false)
    {
        // Busca nova (outro texto ou filtro) volta para a primeira página.
        if (!keepLimit && !keepSelection)
            _searchLimit = SearchPageSize;
        var query = CurrentQuery();
        var (since, until) = PeriodBounds(SearchPeriod, SearchFromDate, SearchToDate, DateTimeOffset.Now);
        _searchCts?.Cancel();
        // Só o período (sem texto) também busca: lista as mensagens daquelas datas.
        if (query.IsEmpty && since is null && until is null)
        {
            if (IsSearchActive)
            {
                IsSearchActive = false;
                if (SelectedNode is { IsSelectable: true } node)
                    await LoadListAsync(node);
            }
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        IsSearchActive = true;
        IsListLoading = true;
        ListError = null;
        ListTitle = Strings.Search_Title;
        try
        {
            var folderId = SearchInFolder && SelectedNode is FolderNode f ? f.Folder.Id : (Guid?)null;
            var search = new MessageSearch(query, AccountId: folderId is null ? SearchAccount?.Account?.Id : null, FolderId: folderId,
                Since: since, Until: until);
            var found = await _mailbox.SearchAsync(search, _searchLimit, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            var filter = Filter;
            var keepId = keepSelection ? (SelectedRow as MessageRow)?.Summary.Id : null;
            var rows = await Task.Run(() => MessageListBuilder.Build(ApplyFilter(found, filter, keepId),
                _accounts, _locallyRead, _time.GetLocalNow(), _loc.FormatCulture), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _listMessages = found;
            if (keepSelection)
            {
                ReplaceRowsKeepingSelection(rows);
            }
            else
            {
                SelectedRow = null;
                Rows = rows;
            }
            CanLoadMoreResults = found.Count >= _searchLimit;
            ListSubtitle = _loc.Plural("Search_ResultCount", Rows.Count(r => r is MessageRow)) + (CanLoadMoreResults ? "+" : "");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha na busca");
            if (!cts.IsCancellationRequested)
            {
                Rows = [];
                ListError = Strings.Search_Failed;
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsListLoading = false;
        }
        OnPropertyChanged(nameof(CanSearchServer));
    }

    /// <summary>Limpa a busca e o filtro avançado e volta para a pasta aberta.</summary>
    [RelayCommand]
    private Task ClearSearch()
    {
        ResetSearchFields();
        _searchTimer?.Stop();
        return RunSearchAsync();
    }

    private void ResetSearchFields()
    {
        _resettingSearch = true;
        try
        {
            SearchText = "";
            SearchFrom = "";
            SearchTo = "";
            SearchSubject = "";
            SearchHasAttachment = false;
            SearchUnread = false;
            SearchPeriod = SearchPeriods[0];
            SearchFromDate = null;
            SearchToDate = null;
            SearchInFolder = false;
            SearchAccount = null;
            CanLoadMoreResults = false;
            _searchLimit = SearchPageSize;
        }
        finally
        {
            _resettingSearch = false;
        }
    }

    /// <summary>Trocar de pasta encerra a busca sem recarregar duas vezes.</summary>
    private void EndSearchForNavigation()
    {
        if (!IsSearchActive && SearchText.Length == 0)
            return;
        _searchCts?.Cancel();
        _searchTimer?.Stop();
        ResetSearchFields();
        IsSearchActive = false;
    }

    /// <summary>Busca no servidor: na pasta aberta, ou na caixa de entrada de cada conta.</summary>
    [RelayCommand]
    private async Task SearchServerAsync()
    {
        var query = CurrentQuery();
        if (!query.HasText)
            return;
        IsSearchingServer = true;
        var total = 0;
        try
        {
            // Pasta aberta: só nela. Senão, em cada conta (ou na escolhida): no Gmail, "Todos os e-mails" (cobre a
            // caixa inteira); nas demais, Caixa de entrada e Arquivo.
            IEnumerable<Guid> folders = SelectedNode is FolderNode f && SearchInFolder
                ? [f.Folder.Id]
                : _folderNodes.Values
                    .Where(n => SearchAccount?.Account is not { } only || n.Account.Id == only.Id)
                    .Where(n => n.Account.Provider == MailProvider.Gmail
                        ? n.Folder.Kind == SpecialFolderKind.Archive
                        : n.Folder.Kind is SpecialFolderKind.Inbox or SpecialFolderKind.Archive)
                    .Select(n => n.Folder.Id).ToList();
            foreach (var folderId in folders)
                total += await _mailbox.SearchServerAsync(folderId, query);
            ShowStatus(total > 0 ? _loc.Plural("Search_ServerFound", total) : Strings.Search_ServerNothing);
            await RunSearchAsync(keepSelection: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha na busca no servidor");
            ShowStatus(Strings.Search_ServerFailed);
        }
        finally
        {
            IsSearchingServer = false;
        }
    }
}

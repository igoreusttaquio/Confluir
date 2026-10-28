using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.Core.Models;
using Confluir.Core.Search;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

/// <param name="Days">Últimos N dias; 0 = qualquer data.</param>
public sealed record SearchPeriodOption(int Days, string LabelKey);

/// <summary>
/// Busca: campo no topo da lista (com operadores como <c>de:</c> e <c>assunto:</c>) e filtro avançado
/// (remetente, destinatário, assunto, anexo, não lidas, período, só nesta pasta). Os resultados ocupam a lista,
/// com os filtros rápidos valendo por cima. Sem resultado local, dá para buscar no servidor.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _searchTimer;
    private bool _resettingSearch;

    public IReadOnlyList<SearchPeriodOption> SearchPeriods { get; } =
    [
        new(0, nameof(Strings.Search_Period_Any)),
        new(7, nameof(Strings.Search_Period_Week)),
        new(30, nameof(Strings.Search_Period_Month)),
        new(365, nameof(Strings.Search_Period_Year)),
    ];

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
    partial void OnSearchPeriodChanged(SearchPeriodOption? value) => ScheduleSearch();
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

    private async Task RunSearchAsync(bool keepSelection = false)
    {
        var query = CurrentQuery();
        _searchCts?.Cancel();
        if (query.IsEmpty)
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
            var days = SearchPeriod?.Days ?? 0;
            var folderId = SearchInFolder && SelectedNode is FolderNode f ? f.Folder.Id : (Guid?)null;
            var search = new MessageSearch(query, FolderId: folderId,
                Since: days > 0 ? DateTimeOffset.Now.AddDays(-days) : null);
            var found = await _mailbox.SearchAsync(search, 500, cts.Token);
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
            ListSubtitle = _loc.Plural("Search_ResultCount", Rows.Count(r => r is MessageRow));
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
            SearchInFolder = false;
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
            IEnumerable<Guid> folders = SelectedNode is FolderNode f
                ? [f.Folder.Id]
                : _folderNodes.Values.Where(n => n.Folder.Kind == SpecialFolderKind.Inbox).Select(n => n.Folder.Id).ToList();
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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.Core.Models;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

/// <summary>
/// Seleção de várias mensagens (Shift/Ctrl na lista) e ações em lote, também usadas pelo menu de contexto:
/// a ação vale para a mensagem clicada ou, se ela faz parte da seleção, para toda a seleção.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<MessageRow> _selectedMessages = [];

    public IReadOnlyList<MessageRow> SelectedMessages => _selectedMessages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionCountText), nameof(HasSingleOrNoSelection))]
    public partial bool HasMultipleSelection { get; private set; }

    public bool HasSingleOrNoSelection => !HasMultipleSelection;

    public string SelectionCountText => string.Format(Strings.Selection_Count, _selectedMessages.Count);

    /// <summary>Rótulos das ações em lote: o que vai acontecer com a seleção.</summary>
    public string SelectionReadText => _selectedMessages.Any(r => !r.IsRead) ? Strings.Action_MarkRead : Strings.Action_MarkUnread;
    public string SelectionFlagText => _selectedMessages.Any(r => !r.IsFlagged) ? Strings.Action_Flag : Strings.Action_Unflag;

    /// <summary>Chamado pela lista a cada mudança de seleção (cabeçalhos de data ficam de fora).</summary>
    public void SetSelection(IEnumerable<object> selected)
    {
        _selectedMessages = selected.OfType<MessageRow>().ToList();
        var multiple = _selectedMessages.Count > 1;
        if (multiple && Detail is not null)
        {
            // Várias selecionadas: o painel de leitura dá lugar às ações em lote.
            _detailCts?.Cancel();
            Detail = null;
        }
        else if (!multiple && HasMultipleSelection && SelectedRow is MessageRow row && !IsTwoColumnLayout)
        {
            Detail = CreateDetail(row);
        }
        HasMultipleSelection = multiple;
        OnPropertyChanged(nameof(SelectedMessages));
        OnPropertyChanged(nameof(SelectionCountText));
        OnPropertyChanged(nameof(SelectionReadText));
        OnPropertyChanged(nameof(SelectionFlagText));
    }

    /// <summary>A mensagem do menu de contexto, ou a seleção inteira se ela faz parte dela.</summary>
    public IReadOnlyList<MessageRow> TargetsFor(MessageRow row) =>
        _selectedMessages.Count > 1 && _selectedMessages.Contains(row) ? _selectedMessages : [row];

    public bool CanArchiveRow(MessageRow row) =>
        _folderNodes.Values.Any(f => f.Account.Id == row.Account.Id && f.Folder.Kind == SpecialFolderKind.Archive) &&
        !(_folderNodes.TryGetValue(row.Summary.FolderId, out var current) && current.Folder.Kind == SpecialFolderKind.Archive);

    public Task ArchiveRowsAsync(IReadOnlyList<MessageRow> rows) =>
        RemoveRowsAsync(rows.Where(CanArchiveRow).ToList(), _mailbox.ArchiveAsync);

    public Task DeleteRowsAsync(IReadOnlyList<MessageRow> rows) => RemoveRowsAsync(rows, _mailbox.DeleteAsync);

    /// <summary>Se alguma não está sinalizada, sinaliza todas; senão tira a sinalização de todas.</summary>
    public async Task ToggleFlagRowsAsync(IReadOnlyList<MessageRow> rows)
    {
        var flag = rows.Any(r => !r.IsFlagged);
        foreach (var row in rows.Where(r => r.IsFlagged != flag))
        {
            row.IsFlagged = flag;
            try
            {
                await _mailbox.SetFlaggedAsync(row.Summary.Id, flag);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao sinalizar mensagem");
                row.IsFlagged = !flag;
            }
        }
        RaiseSelectionLabels();
    }

    /// <summary>Se alguma não foi lida, marca todas como lidas; senão marca todas como não lidas.</summary>
    public async Task ToggleReadRowsAsync(IReadOnlyList<MessageRow> rows)
    {
        var read = rows.Any(r => !r.IsRead);
        foreach (var row in rows.Where(r => r.IsRead != read))
        {
            SetRead(row, read);
            try
            {
                await _mailbox.SetReadAsync(row.Summary.Id, read);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao marcar mensagem como lida/não lida");
                SetRead(row, !read);
            }
        }
        RaiseSelectionLabels();
    }

    private void RaiseSelectionLabels()
    {
        OnPropertyChanged(nameof(SelectionReadText));
        OnPropertyChanged(nameof(SelectionFlagText));
    }

    // Barra de ações em lote ----------------------------------------------------------------------------

    [RelayCommand]
    private Task ArchiveSelected() => ArchiveRowsAsync(_selectedMessages);

    [RelayCommand]
    private Task DeleteSelected() => DeleteRowsAsync(_selectedMessages);

    [RelayCommand]
    private Task ToggleFlagSelected() => ToggleFlagRowsAsync(_selectedMessages);

    [RelayCommand]
    private Task ToggleReadSelected() => ToggleReadRowsAsync(_selectedMessages);

    /// <summary>Pede à lista que desmarque tudo (a lista trata o pedido).</summary>
    public event EventHandler? ClearSelectionRequested;

    [RelayCommand]
    private void ClearSelection() => ClearSelectionRequested?.Invoke(this, EventArgs.Empty);
}

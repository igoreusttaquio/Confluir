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

    // Linhas da lista atual: ao trocar a lista, ela ainda pode avisar a seleção antiga uma última vez antes de
    // limpá-la (sem avisar de novo); essas linhas que já saíram não contam.
    private HashSet<MessageRow> _currentRows = new(ReferenceEqualityComparer.Instance);

    /// <summary>Chamado pela lista a cada mudança de seleção (cabeçalhos de data ficam de fora).</summary>
    public void SetSelection(IEnumerable<object> selected)
    {
        _selectedMessages = selected.OfType<MessageRow>().Where(_currentRows.Contains).ToList();
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
        OnPropertyChanged(nameof(SelectionSpamText));
    }

    /// <summary>
    /// Lista nova (arquivar/excluir, sincronização, busca): a lista perde a seleção sem avisar, então a seleção
    /// múltipla termina aqui (senão a barra "x selecionadas" ficava à vista sem nada selecionado).
    /// </summary>
    partial void OnRowsChanged(IReadOnlyList<MessageListRow> value)
    {
        _currentRows = new HashSet<MessageRow>(value.OfType<MessageRow>(), ReferenceEqualityComparer.Instance);
        EndMultipleSelection();
    }

    /// <summary>Encerra a seleção múltipla no ViewModel, sem depender de a lista avisar (some a barra).</summary>
    private void EndMultipleSelection()
    {
        if (_selectedMessages.Count == 0 && !HasMultipleSelection)
            return;
        _selectedMessages = [];
        HasMultipleSelection = false;
        OnPropertyChanged(nameof(SelectedMessages));
        OnPropertyChanged(nameof(SelectionCountText));
        RaiseSelectionLabels();
    }

    /// <summary>A mensagem do menu de contexto, ou a seleção inteira se ela faz parte dela.</summary>
    public IReadOnlyList<MessageRow> TargetsFor(MessageRow row) =>
        _selectedMessages.Count > 1 && _selectedMessages.Contains(row) ? _selectedMessages : [row];

    public bool CanArchiveRow(MessageRow row) =>
        _folderNodes.Values.Any(f => f.Account.Id == row.Account.Id && f.Folder.Kind == SpecialFolderKind.Archive) &&
        !(_folderNodes.TryGetValue(row.Summary.FolderId, out var current) && current.Folder.Kind == SpecialFolderKind.Archive);

public async Task ArchiveRowsAsync(IReadOnlyList<MessageRow> rows)
    {
        var archivable = rows.Where(CanArchiveRow).ToList();
        if (archivable.Count == 0)
            return;
        var originals = Snapshot(archivable);
        await RemoveRowsAsync(archivable, _mailbox.ArchiveAsync);
        ShowStatus(_loc.Plural("Feedback_Archived", archivable.Count), () => UndoRemovalAsync(originals));
    }

    public async Task DeleteRowsAsync(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0)
            return;
var originals = Snapshot(rows);
        await RemoveRowsAsync(rows, _mailbox.DeleteAsync);
        ShowStatus(_loc.Plural("Feedback_Deleted", originals.Count), () => UndoRemovalAsync(originals));
    }

    private bool IsJunkRow(MessageRow row) =>
        _folderNodes.TryGetValue(row.Summary.FolderId, out var folder) && folder.Folder.Kind == SpecialFolderKind.Junk;

    private bool HasFolder(MessageRow row, SpecialFolderKind kind) =>
        _folderNodes.Values.Any(f => f.Account.Id == row.Account.Id && f.Folder.Kind == kind);

    /// <summary>Todas já estão no Spam: a ação vira "Não é spam" (volta para a caixa de entrada).</summary>
    public bool AreAllJunk(IReadOnlyList<MessageRow> rows) => rows.Count > 0 && rows.All(IsJunkRow);

    public string SpamActionText(IReadOnlyList<MessageRow> rows) =>
        AreAllJunk(rows) ? Strings.Action_NotSpam : Strings.Action_MarkSpam;

    public bool CanSpamRows(IReadOnlyList<MessageRow> rows) =>
        rows.Any(r => HasFolder(r, AreAllJunk(rows) ? SpecialFolderKind.Inbox : SpecialFolderKind.Junk));

    public string SelectionSpamText => SpamActionText(_selectedMessages);

    /// <summary>Spam ou "não é spam", conforme <see cref="AreAllJunk"/>; com aviso e Desfazer, como arquivar.</summary>
    public async Task ToggleSpamRowsAsync(IReadOnlyList<MessageRow> rows)
    {
        var notSpam = AreAllJunk(rows);
        var targets = rows.Where(r => IsJunkRow(r) == notSpam &&
                                      HasFolder(r, notSpam ? SpecialFolderKind.Inbox : SpecialFolderKind.Junk)).ToList();
        if (targets.Count == 0)
            return;
        var originals = Snapshot(targets);
        await RemoveRowsAsync(targets, (id, ct) => _mailbox.SetJunkAsync(id, !notSpam, ct));
        ShowStatus(_loc.Plural(notSpam ? "Feedback_NotSpam" : "Feedback_Spam", targets.Count), () => UndoRemovalAsync(originals));
    }

    /// <summary>Estado atual das mensagens (lida/sinalizada podem ter mudado nesta sessão), para devolvê-las iguais.</summary>
    private static List<MessageSummary> Snapshot(IReadOnlyList<MessageRow> rows) =>
        rows.Select(r => r.Summary with { IsRead = r.IsRead, IsFlagged = r.IsFlagged }).ToList();

    /// <summary>Desfazer arquivar/excluir: volta o que ainda não foi ao servidor e recarrega a lista.</summary>
    private async Task UndoRemovalAsync(IReadOnlyList<MessageSummary> messages)
    {
        var restored = 0;
        foreach (var message in messages)
        {
            try
            {
                if (await _mailbox.UndoRemoveAsync(message))
                    restored++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao desfazer a remoção");
            }
        }
        RefreshList();
        if (restored < messages.Count)
            ShowStatus(Strings.Undo_TooLate);
    }

    /// <summary>Se alguma não está sinalizada, sinaliza todas; senão tira a sinalização de todas.</summary>
    public Task ToggleFlagRowsAsync(IReadOnlyList<MessageRow> rows) => SetFlagRowsAsync(rows, rows.Any(r => !r.IsFlagged), feedback: true);

    private async Task SetFlagRowsAsync(IReadOnlyList<MessageRow> rows, bool flag, bool feedback)
    {
        var changed = rows.Where(r => r.IsFlagged != flag).ToList();
        foreach (var row in changed)
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
        if (feedback && changed.Count > 0)
            ShowStatus(_loc.Plural(flag ? "Feedback_Flagged" : "Feedback_Unflagged", changed.Count),
                () => SetFlagRowsAsync(changed, !flag, feedback: false));
    }

    /// <summary>Se alguma não foi lida, marca todas como lidas; senão marca todas como não lidas.</summary>
    public Task ToggleReadRowsAsync(IReadOnlyList<MessageRow> rows) => SetReadRowsAsync(rows, rows.Any(r => !r.IsRead), feedback: true);

    private async Task SetReadRowsAsync(IReadOnlyList<MessageRow> rows, bool read, bool feedback)
    {
        var changed = rows.Where(r => r.IsRead != read).ToList();
        foreach (var row in changed)
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
        if (feedback && changed.Count > 0)
            ShowStatus(_loc.Plural(read ? "Feedback_Read" : "Feedback_Unread", changed.Count),
                () => SetReadRowsAsync(changed, !read, feedback: false));
    }

    private void RaiseSelectionLabels()
    {
        OnPropertyChanged(nameof(SelectionReadText));
        OnPropertyChanged(nameof(SelectionFlagText));
        OnPropertyChanged(nameof(SelectionSpamText));
    }

    // Barra de ações em lote ----------------------------------------------------------------------------

    [RelayCommand]
    private Task ArchiveSelected() => ArchiveRowsAsync(_selectedMessages);

    [RelayCommand]
    private Task DeleteSelected() => DeleteRowsAsync(_selectedMessages);

    // Sinalizar e marcar lida mantêm as mensagens na lista: a seleção termina depois, como nas outras ações.

    [RelayCommand]
    private async Task ToggleFlagSelected()
    {
        await ToggleFlagRowsAsync(_selectedMessages);
        ClearSelection();
    }

    [RelayCommand]
    private async Task ToggleReadSelected()
    {
        await ToggleReadRowsAsync(_selectedMessages);
        ClearSelection();
    }

    [RelayCommand]
    private Task ToggleSpamSelected() => ToggleSpamRowsAsync(_selectedMessages);

    /// <summary>Pede à lista que desmarque tudo (a lista trata o pedido).</summary>
    public event EventHandler? ClearSelectionRequested;

    /// <summary>O X da barra: a barra some mesmo que a lista já não tenha nada selecionado (e não avise).</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        ClearSelectionRequested?.Invoke(this, EventArgs.Empty);
        EndMultipleSelection();
    }
}

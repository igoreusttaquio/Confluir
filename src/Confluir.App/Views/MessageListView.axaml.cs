using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Confluir.App.Resources;
using Confluir.App.ViewModels;
using Lucide.Avalonia;

namespace Confluir.App.Views;

public partial class MessageListView : UserControl
{
    private MainWindowViewModel? _bound;
    private MessageRow? _contextRow;
    private bool _menuReady;

    public MessageListView()
    {
        InitializeComponent();
        // Duas colunas: um clique abre a mensagem. Três colunas: clique duplo abre em tela própria.
        Messages.Tapped += (_, e) => OpenIf(e, ViewModel?.IsTwoColumnLayout == true && !HasModifier(e));
        Messages.DoubleTapped += (_, e) => OpenIf(e, true);
        Messages.KeyDown += OnKeyDown;
        // Shift/Ctrl selecionam várias; a seleção vai para o ViewModel (ações em lote).
        Messages.SelectionChanged += (_, _) => ViewModel?.SetSelection(Messages.SelectedItems?.OfType<object>() ?? []);
        // Menu do botão direito: os itens são montados ANTES de o menu abrir (no clique ou na tecla de menu).
        // Montados durante a abertura, o popup já foi medido vazio e aparece com tamanho zero.
        Messages.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(Messages).Properties.IsRightButtonPressed)
                return;
            _contextRow = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MessageRow;
            _menuReady = _contextRow is not null && FillContextMenu(RowMenu);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Messages.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                _contextRow = null; // vale a selecionada
                _menuReady = FillContextMenu(RowMenu);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        RowMenu.Opening += (_, e) =>
        {
            if (!_menuReady)
                e.Cancel = true;
        };
        RowMenu.Closed += (_, _) =>
        {
            _contextRow = null;
            _menuReady = false;
        };

        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ViewModel?.SearchNowCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ViewModel is { IsSearchActive: true } vm)
            {
                vm.ClearSearchCommand.Execute(null);
                Messages.Focus();
                e.Handled = true;
            }
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_bound is not null)
            _bound.ClearSelectionRequested -= OnClearSelectionRequested;
        _bound = ViewModel;
        if (_bound is not null)
            _bound.ClearSelectionRequested += OnClearSelectionRequested;
    }

    /// <summary>Ctrl+F (atalho da janela).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnClearSelectionRequested(object? sender, EventArgs e) => Messages.SelectedItems?.Clear();

    private static bool HasModifier(TappedEventArgs e) =>
        (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    private void OpenIf(RoutedEventArgs e, bool condition)
    {
        // Só cliques em linhas de mensagem (não em cabeçalhos de data nem na barra de rolagem).
        if (!condition || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not MessageRow)
            return;
        if (ViewModel?.HasMultipleSelection == true)
            return;
        ViewModel?.OpenMessageCommand.Execute(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel?.SelectedRow is MessageRow && ViewModel.HasMultipleSelection == false)
        {
            ViewModel.OpenMessageCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Botão direito numa mensagem: arquivar, excluir, sinalizar, marcar como lida/não lida.
    /// Se a mensagem faz parte de uma seleção, a ação vale para todas as selecionadas.
    /// </summary>
    private bool FillContextMenu(ContextMenu menu)
    {
        // Pelo teclado (Shift+F10 / tecla de menu) vale a mensagem selecionada.
        var row = _contextRow ?? ViewModel?.SelectedRow as MessageRow;
        if (ViewModel is not { } vm || row is null)
            return false;
        if (Messages.SelectedItems?.Contains(row) != true)
            Messages.SelectedItem = row;

        var targets = vm.TargetsFor(row);
        var suffix = targets.Count > 1 ? $" ({targets.Count})" : "";
        var anyUnread = targets.Any(r => !r.IsRead);
        var anyUnflagged = targets.Any(r => !r.IsFlagged);

        var items = new List<Control>();
        if (targets.Count == 1)
        {
            items.Add(Item(Strings.Action_Reply, LucideIconKind.Reply, () => _ = vm.ReplyToRowAsync(row)));
            items.Add(new Separator());
        }
        if (targets.Any(vm.CanArchiveRow))
            items.Add(Item(Strings.Action_Archive + suffix, LucideIconKind.Archive, () => _ = vm.ArchiveRowsAsync(targets)));
        items.Add(Item(Strings.Action_Delete + suffix, LucideIconKind.Trash, () => _ = vm.DeleteRowsAsync(targets)));
        items.Add(Item((anyUnflagged ? Strings.Action_Flag : Strings.Action_Unflag) + suffix, LucideIconKind.Flag,
            () => _ = vm.ToggleFlagRowsAsync(targets)));
        items.Add(Item((anyUnread ? Strings.Action_MarkRead : Strings.Action_MarkUnread) + suffix,
            anyUnread ? LucideIconKind.MailOpen : LucideIconKind.Mail, () => _ = vm.ToggleReadRowsAsync(targets)));
        if (targets.Count == 1)
        {
            items.Add(new Separator());
            items.Add(Item(Strings.Rule_FromMessage, LucideIconKind.ListFilter, () => vm.CreateRuleForRow(row)));
        }

        menu.Items.Clear();
        foreach (var control in items)
            menu.Items.Add(control);
        return true;
    }

    private static MenuItem Item(string header, LucideIconKind icon, Action action)
    {
        var item = new MenuItem { Header = header, Icon = new LucideIcon { Kind = icon, Size = 16 } };
        item.Click += (_, _) => action();
        return item;
    }
}

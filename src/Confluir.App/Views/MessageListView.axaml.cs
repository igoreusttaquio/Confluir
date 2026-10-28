using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

public partial class MessageListView : UserControl
{
    private MainWindowViewModel? _bound;

    public MessageListView()
    {
        InitializeComponent();
        // Duas colunas: um clique abre a mensagem. Três colunas: clique duplo abre em tela própria.
        Messages.Tapped += (_, e) => OpenIf(e, ViewModel?.IsTwoColumnLayout == true && !HasModifier(e));
        Messages.DoubleTapped += (_, e) => OpenIf(e, true);
        Messages.KeyDown += OnKeyDown;
        // Shift/Ctrl selecionam várias; a seleção vai para o ViewModel (ações em lote).
        Messages.SelectionChanged += (_, _) => ViewModel?.SetSelection(Messages.SelectedItems?.OfType<object>() ?? []);
        // O menu recebe a mensagem antes de abrir: a clicada com o botão direito, ou a selecionada pelo teclado.
        Messages.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Messages).Properties.IsRightButtonPressed)
                SetMenuRow((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MessageRow);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Messages.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
                SetMenuRow(ViewModel?.SelectedRow as MessageRow);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

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

    // Menu de contexto das mensagens ------------------------------------------------------------------------

    /// <summary>
    /// Ao abrir: a mensagem clicada passa a ser a selecionada, a menos que já faça parte de uma seleção
    /// (aí o menu vale para todas). Responder e criar regra só aparecem para uma mensagem.
    /// </summary>
    private void OnRowMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || RowMenuRow is not { } row || ViewModel is not { } vm)
        {
            (sender as MenuFlyout)?.Hide();
            return;
        }
        if (Messages.SelectedItems?.Contains(row) != true)
            Messages.SelectedItem = row;
        var several = vm.TargetsFor(row).Count > 1;
        foreach (var control in menu.Items.OfType<Control>())
        {
            if (control.Classes.Contains("single"))
                control.IsVisible = !several;
        }
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Icon is Lucide.Avalonia.LucideIcon { Kind: Lucide.Avalonia.LucideIconKind.Archive }) is { } archive)
            archive.IsEnabled = vm.TargetsFor(row).Any(vm.CanArchiveRow);
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Icon is Lucide.Avalonia.LucideIcon { Kind: Lucide.Avalonia.LucideIconKind.ShieldAlert }) is { } spam)
        {
            var targets = vm.TargetsFor(row);
            spam.Header = vm.SpamActionText(targets);
            spam.IsEnabled = vm.CanSpamRows(targets);
        }
    }

    /// <summary>A mensagem do menu: os itens (MenuItem) recebem-na como DataContext para os rótulos.</summary>
    private MessageRow? RowMenuRow { get; set; }

    private MenuFlyout RowMenu => (MenuFlyout)Messages.ContextFlyout!;

    private void SetMenuRow(MessageRow? row)
    {
        RowMenuRow = row;
        foreach (var item in RowMenu.Items.OfType<Control>())
            item.DataContext = row;
    }

    private (MainWindowViewModel Vm, IReadOnlyList<MessageRow> Targets, MessageRow Row)? Targets(object? sender) =>
        RowMenuRow is { } row && ViewModel is { } vm ? (vm, vm.TargetsFor(row), row) : null;

    private void OnMenuReply(object? sender, RoutedEventArgs e)
    {
        if (Targets(sender) is { } t)
            _ = t.Vm.ReplyToRowAsync(t.Row);
    }

    private void OnMenuSpam(object? sender, RoutedEventArgs e)
    {
        if (Targets(sender) is { } t)
            _ = t.Vm.ToggleSpamRowsAsync(t.Targets);
    }

    private void OnMenuArchive(object? sender, RoutedEventArgs e)
    {
        if (Targets(sender) is { } t)
            _ = t.Vm.ArchiveRowsAsync(t.Targets);
    }

    private void OnMenuDelete(object? sender, RoutedEventArgs e)
    {
        if (Targets(sender) is { } t)
            _ = t.Vm.DeleteRowsAsync(t.Targets);
    }

    private void OnMenuFlag(object? sender, RoutedEventArgs e)
    {
        if (Targets(sender) is { } t)
            _ = t.Vm.ToggleFlagRowsAsync(t.Targets);
    }

    private void OnMenuRead(object? sender, RoutedEventArgs e)
    {
        if (Targets(sender) is { } t)
            _ = t.Vm.ToggleReadRowsAsync(t.Targets);
    }

    private void OnMenuRule(object? sender, RoutedEventArgs e)
    {
        if (Targets(sender) is { } t)
            t.Vm.CreateRuleForRow(t.Row);
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

public partial class MessageListView : UserControl
{
    public MessageListView()
    {
        InitializeComponent();
        // Duas colunas: um clique abre a mensagem. Três colunas: clique duplo abre em tela própria.
        Messages.Tapped += (_, e) => OpenIf(e, ViewModel?.IsTwoColumnLayout == true);
        Messages.DoubleTapped += (_, e) => OpenIf(e, true);
        Messages.KeyDown += OnKeyDown;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OpenIf(RoutedEventArgs e, bool condition)
    {
        // Só cliques em linhas de mensagem (não em cabeçalhos de data nem na barra de rolagem).
        if (!condition || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not MessageRow)
            return;
        ViewModel?.OpenMessageCommand.Execute(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel?.SelectedRow is MessageRow)
        {
            ViewModel.OpenMessageCommand.Execute(null);
            e.Handled = true;
        }
    }
}

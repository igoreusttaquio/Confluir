using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

public partial class SidebarView : UserControl
{
    // Conta do menu do botão direito (a linha da lista captura o ponteiro, por isso o menu fica na lista toda).
    private AccountNode? _menuAccount;

    public SidebarView()
    {
        InitializeComponent();
        Nodes.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Nodes).Properties.IsRightButtonPressed)
                SetMenuTarget((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Nodes.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
                SetMenuTarget((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private MenuFlyout? _accountMenu;

    // Menu conforme a linha: conta, pasta criada pelo usuário (excluir) ou nenhum (caixa unificada e pastas especiais).
    private void SetMenuTarget(object? node)
    {
        _accountMenu ??= Nodes.ContextFlyout as MenuFlyout;
        var folderMenu = Nodes.Resources["FolderMenu"] as MenuFlyout;
        _menuAccount = node as AccountNode;
        var folder = node is FolderNode { CanDelete: true } f ? f : null;
        Nodes.ContextFlyout = _menuAccount is not null ? _accountMenu : folder is not null ? folderMenu : null;
        foreach (var (menu, context) in new[] { (_accountMenu, (object?)_menuAccount), (folderMenu, folder) })
        {
            if (menu is null)
                continue;
            foreach (var item in menu.Items.OfType<Control>())
                item.DataContext = context;
        }
    }

    // O menu de conta só abre sobre uma conta.
    private void OnAccountMenuOpening(object? sender, EventArgs e)
    {
        if (_menuAccount is null && sender is MenuFlyout menu)
            menu.Hide();
    }

    // O campo "Nova pasta" recebe o foco quando aparece.
    private void OnNewFolderBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { DataContext: AccountNode node } box)
        {
            void OnChanged(object? s, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(AccountNode.IsCreatingFolder) && node.IsCreatingFolder)
                    Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
            }
            node.PropertyChanged += OnChanged;
            box.Tag = (node, (PropertyChangedEventHandler)OnChanged);
        }
    }

    private void OnNewFolderBoxDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { Tag: (AccountNode node, PropertyChangedEventHandler handler) } box)
        {
            node.PropertyChanged -= handler;
            box.Tag = null;
        }
    }
}

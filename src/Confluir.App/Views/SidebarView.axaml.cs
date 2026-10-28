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
                SetMenuAccount((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as AccountNode);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Nodes.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
                SetMenuAccount((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as AccountNode);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private MenuFlyout? _accountMenu;

    // Pastas e a caixa unificada não têm menu: o menu só fica na lista enquanto o clique é numa conta.
    private void SetMenuAccount(AccountNode? account)
    {
        _accountMenu ??= Nodes.ContextFlyout as MenuFlyout;
        _menuAccount = account;
        Nodes.ContextFlyout = account is null ? null : _accountMenu;
        if (_accountMenu is not null)
        {
            foreach (var item in _accountMenu.Items.OfType<Control>())
                item.DataContext = account;
        }
    }

    // Só abre sobre uma conta (pastas e a caixa unificada não têm menu).
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

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

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

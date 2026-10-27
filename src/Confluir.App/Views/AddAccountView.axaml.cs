using Avalonia.Controls;

namespace Confluir.App.Views;

public partial class AddAccountView : UserControl
{
    public AddAccountView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => EmailBox.Focus();
    }
}

using Avalonia;
using Avalonia.Controls;

namespace Confluir.App.Views;

public partial class AccountEditorView : UserControl
{
    // Abaixo desta largura a ajuda lateral passa para baixo do formulário.
    private const double WideLayoutMinWidth = 1000;

    public AccountEditorView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => EmailBox.Focus();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var wide = e.NewSize.Width >= WideLayoutMinWidth;
        Grid.SetRow(Aside, wide ? 1 : 2);
        Grid.SetColumn(Aside, wide ? 1 : 0);
        Aside.Width = wide ? 280 : double.NaN;
        Aside.MaxWidth = wide ? double.PositiveInfinity : 640;
        Aside.Margin = wide ? new Thickness(40, 4, 0, 0) : new Thickness(0, 32, 0, 0);
        Aside.HorizontalAlignment = wide ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
    }
}

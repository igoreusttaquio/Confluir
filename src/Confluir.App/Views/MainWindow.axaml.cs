using Avalonia.Controls;
using Confluir.App.ViewModels;
using Confluir.Core.Settings;

namespace Confluir.App.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsStore? _settings;

    // Usado pelo previewer do XAML.
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel, ISettingsStore settings) : this()
    {
        _settings = settings;
        DataContext = viewModel;

        var layout = settings.Current.Layout;
        Columns.ColumnDefinitions[0].Width = new GridLength(layout.SidebarWidth);
        Columns.ColumnDefinitions[2].Width = new GridLength(layout.MessageListWidth);

        Opened += async (_, _) => await viewModel.InitializeAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_settings is null || e.Cancel)
            return;

        _settings.Current.Layout.SidebarWidth = Columns.ColumnDefinitions[0].ActualWidth;
        _settings.Current.Layout.MessageListWidth = Columns.ColumnDefinitions[2].ActualWidth;
        _settings.Save();
    }
}

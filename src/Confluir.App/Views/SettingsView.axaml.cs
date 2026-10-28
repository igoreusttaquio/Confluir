using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Confluir.App.Resources;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        GoogleBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ViewModel?.Fonts.ImportGoogleCommand.CanExecute(null) == true)
            {
                ViewModel.Fonts.ImportGoogleCommand.Execute(null);
                e.Handled = true;
            }
        };

        // Gravando um atalho: a tecla vai para a linha (e não dispara o atalho atual, nem Esc volta de tela). Escuta na
        // janela, porque o foco pode não estar dentro desta tela.
        AttachedToVisualTree += (_, _) =>
        {
            _keyHost = TopLevel.GetTopLevel(this);
            _keyHost?.AddHandler(KeyDownEvent, OnRecordKeyDown, RoutingStrategies.Tunnel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _keyHost?.RemoveHandler(KeyDownEvent, OnRecordKeyDown);
            _keyHost = null;
        };
    }

    private TopLevel? _keyHost;

    private void OnRecordKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || ViewModel?.RecordingShortcut is not { } item)
            return;
        item.Capture(e.Key, e.KeyModifiers);
        e.Handled = true;
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnImportFileClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || ViewModel is not { } vm)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = Strings.Fonts_ImportFile,
            FileTypeFilter = [new FilePickerFileType(Strings.Fonts_FileType) { Patterns = ["*.ttf", "*.otf", "*.zip"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await vm.Fonts.ImportFilesAsync(paths);
    }

    private void OnOpenDataFolder(object? sender, RoutedEventArgs e) => OpenFolder(ViewModel?.DataDirectory);

    private void OnOpenLogsFolder(object? sender, RoutedEventArgs e) => OpenFolder(ViewModel?.LogsDirectory);

    private void OpenFolder(string? path)
    {
        if (path is null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        Directory.CreateDirectory(path);
        _ = top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}

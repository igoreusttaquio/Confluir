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

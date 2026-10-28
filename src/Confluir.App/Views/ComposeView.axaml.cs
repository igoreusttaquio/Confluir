using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Confluir.App.Resources;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

public partial class ComposeView : UserControl
{
    public ComposeView()
    {
        InitializeComponent();
        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.AsyncPopulator = (text, ct) => ViewModel?.SuggestAsync(text, ct) ?? Task.FromResult<IEnumerable<object>>([]);
            // Escolher uma sugestão troca só o último destinatário digitado e deixa pronto para o próximo.
            box.TextSelector = (text, item) =>
            {
                var current = text ?? "";
                var cut = current.LastIndexOfAny([',', ';']);
                var prefix = cut >= 0 ? current[..(cut + 1)] + " " : "";
                return prefix + item + ", ";
            };
        }

        Root.AddHandler(DragDrop.DragEnterEvent, (_, e) => SetDropping(e, true));
        Root.AddHandler(DragDrop.DragOverEvent, (_, e) => SetDropping(e, true));
        Root.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.Classes.Set("active", false));
        Root.AddHandler(DragDrop.DropEvent, OnDrop);

        AttachedToVisualTree += (_, _) =>
        {
            if (ViewModel?.FocusBody == true)
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0; // escreve acima da citação
            }
            else
            {
                ToBox.Focus();
            }
        };
    }

    private ComposeViewModel? ViewModel => DataContext as ComposeViewModel;

    private void SetDropping(DragEventArgs e, bool dropping)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Classes.Set("active", dropping && hasFiles);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.Classes.Set("active", false);
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;
        ViewModel?.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = Strings.Compose_Attach,
        });
        ViewModel?.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Confluir.App.Views;

public partial class ComposeView : UserControl
{
    private ComposeViewModel? _bound;

    public ComposeView()
    {
        InitializeComponent();
        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.AsyncPopulator = (text, ct) => ViewModel?.SuggestAsync(text, ct) ?? Task.FromResult<IEnumerable<object>>([]);
            // Busca só depois de uma pausa na digitação (não a cada tecla); a anterior é cancelada.
            box.MinimumPopulateDelay = TimeSpan.FromMilliseconds(200);
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

        // Botões de formatação: o comando vai no Tag.
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: string command } button && button.Classes.Contains("fmt"))
                Editor.Exec(command);
        });

        Editor.ThemeService = (Application.Current as App)?.Services.GetService<ThemeService>();
        Editor.Placeholder = Strings.Compose_BodyPlaceholder;
        Editor.ContentChanged += (_, html) => ViewModel?.OnEditorContent(html);
        Editor.Dirty += (_, _) => ViewModel?.OnEditorDirty();
        Editor.FormatChanged += (_, s) =>
        {
            BoldButton.Classes.Set("on", s.Bold);
            ItalicButton.Classes.Set("on", s.Italic);
            UnderlineButton.Classes.Set("on", s.Underline);
            StrikeButton.Classes.Set("on", s.Strike);
            BulletButton.Classes.Set("on", s.BulletList);
            NumberedButton.Classes.Set("on", s.NumberedList);
        };
        Editor.FileDropped += async (_, f) =>
        {
            if (ViewModel is { } vm)
                await vm.AddFileContentAsync(f.Name, f.Data);
        };
        Editor.FileTooLarge += (_, name) => ViewModel?.ShowTooLarge(name);
        Editor.SendRequested += (_, _) => ViewModel?.SendCommand.Execute(null);
        Editor.EscapeRequested += (_, _) => ViewModel?.BackCommand.Execute(null);

        LinkBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                OnLinkInsert(null, e);
            else if (e.Key == Key.Escape)
                OnLinkCancel(null, e);
            else
                return;
            e.Handled = true;
        };

        AttachedToVisualTree += (_, _) => FocusInitial();
    }

    private ComposeViewModel? ViewModel => DataContext as ComposeViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_bound is not null)
        {
            _bound.HtmlReplaced -= OnHtmlReplaced;
            _bound.FlushEditor = null;
        }
        _bound = ViewModel;
        if (_bound is null)
            return;
        _bound.HtmlReplaced += OnHtmlReplaced;
        _bound.FlushEditor = Editor.FlushAsync;
        Editor.SetContent(_bound.Html);
    }

    private void OnHtmlReplaced(object? sender, EventArgs e)
    {
        if (_bound is not null)
            Editor.SetContent(_bound.Html);
    }

    private void FocusInitial()
    {
        if (ViewModel is not { } vm)
            return;
        if (!vm.FocusBody)
        {
            ToBox.Focus();
            return;
        }
        if (vm.IsRichText)
        {
            Editor.FocusStart();
        }
        else
        {
            BodyBox.Focus();
            BodyBox.CaretIndex = 0; // escreve acima da citação
        }
    }

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

    // Link inline ------------------------------------------------------------------------------------

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        LinkRow.IsVisible = true;
        LinkBox.Text = "";
        LinkBox.Focus();
    }

    private void OnLinkCancel(object? sender, RoutedEventArgs e)
    {
        LinkRow.IsVisible = false;
        Editor.FocusEditor();
    }

    private void OnLinkInsert(object? sender, RoutedEventArgs e)
    {
        var url = (LinkBox.Text ?? "").Trim();
        LinkRow.IsVisible = false;
        if (url.Length == 0)
        {
            Editor.FocusEditor();
            return;
        }
        // Sem esquema, vira https (ou mailto para endereços de email).
        if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            url = url.Contains('@') && !url.Contains('/') ? "mailto:" + url : "https://" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            Editor.InsertLink(uri.ToString());
        else
            Editor.FocusEditor();
    }
}

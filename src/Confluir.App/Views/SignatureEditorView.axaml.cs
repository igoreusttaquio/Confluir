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

public partial class SignatureEditorView : UserControl
{
    private SignatureEditorViewModel? _bound;

    public SignatureEditorView()
    {
        InitializeComponent();

        // Botões de formatação: o comando vai no Tag (como na escrita de mensagens).
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: string command } button && button.Classes.Contains("fmt"))
                Editor.Exec(command);
        });

        Editor.ThemeService = (Application.Current as App)?.Services.GetService<ThemeService>();
        Colors.Editor = Editor;
        ImageBar.Editor = Editor;
        Editor.FloatedImagesLead = true;
        Editor.Placeholder = Strings.Signature_Placeholder;
        Editor.ContentChanged += (_, html) =>
        {
            if (ViewModel is { } vm)
                vm.Html = html;
        };
        Editor.FormatChanged += (_, s) =>
        {
            BoldButton.Classes.Set("on", s.Bold);
            ItalicButton.Classes.Set("on", s.Italic);
            UnderlineButton.Classes.Set("on", s.Underline);
        };
        // Arquivo solto: se for imagem, entra na assinatura (não há anexos aqui).
        Editor.FileDropped += (_, f) => InsertImage(f.Name, f.Data);
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
    }

    private SignatureEditorViewModel? ViewModel => DataContext as SignatureEditorViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_bound is not null)
            _bound.FlushEditor = null;
        _bound = ViewModel;
        if (_bound is null)
            return;
        _bound.FlushEditor = Editor.FlushAsync;
        Editor.SetContent(_bound.Html);
    }

    private async void OnImageClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Strings.Signature_InsertImage,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.FirstOrDefault() is not { } file)
            return;
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        InsertImage(file.Name, buffer.ToArray());
    }

    private void InsertImage(string name, byte[] data)
    {
        if (ViewModel?.ImageDataUri(name, data) is { } uri)
            Editor.Exec("insertImage", uri);
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

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Confluir.App.Services;
using Confluir.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Confluir.App.Views;

/// <summary>Formatação no ponto do cursor (para destacar os botões da barra).</summary>
public sealed record EditorFormatState(bool Bold, bool Italic, bool Underline, bool Strike, bool BulletList, bool NumberedList);

/// <summary>
/// Editor de texto formatado: um <c>contenteditable</c> num WebView nativo (<see cref="EditorDocument"/>).
/// Fala com o app por mensagens JSON (conteúdo, estado da formatação, arquivos soltos, atalhos).
/// </summary>
public partial class RichEditorView : UserControl
{
    /// <summary>Falso enquanto outra tela estiver por cima (o WebView é uma janela nativa e não some com transições).</summary>
    public static readonly StyledProperty<bool> IsWebAllowedProperty =
        AvaloniaProperty.Register<RichEditorView, bool>(nameof(IsWebAllowed), defaultValue: true);

    private readonly NativeWebView? _web;
    private bool _ready;
    private string? _pendingContent;
    private bool _pendingFocusStart;
    private TaskCompletionSource? _flush;

    public RichEditorView()
    {
        InitializeComponent();
        if (!WebViewSupport.IsAvailable)
            return;

        _web = new NativeWebView { IsVisible = false };
        _web.EnvironmentRequested += OnEnvironmentRequested;
        _web.AdapterCreated += (_, _) => _web.NavigateToString(BuildDocument(), null);
        _web.WebMessageReceived += (_, e) => Dispatcher.UIThread.Post(() => OnMessage(e.Body));
        // O editor nunca navega: links colados não abrem e arquivos soltos são tratados pelo script.
        _web.NavigationStarted += (_, e) =>
        {
            if (e.Request is not null && e.Request.Scheme is not ("about" or "data"))
                e.Cancel = true;
        };
        _web.NewWindowRequested += (_, e) => e.Handled = true;
        Host.Children.Add(_web);
    }

    public bool IsWebAllowed
    {
        get => GetValue(IsWebAllowedProperty);
        set => SetValue(IsWebAllowedProperty, value);
    }

    /// <summary>Tema da página do editor; atribuído antes de carregar.</summary>
    public ThemeService? ThemeService { get; set; }

    public string Placeholder { get; set; } = "";

    public event EventHandler<string>? ContentChanged;

    /// <summary>O texto mudou desde o último conteúdo entregue (o conteúdo em si vem com <see cref="FlushAsync"/>).</summary>
    public event EventHandler? Dirty;
    public event EventHandler<EditorFormatState>? FormatChanged;
    public event EventHandler<(string Name, byte[] Data)>? FileDropped;
    public event EventHandler<string>? FileTooLarge;
    public event EventHandler? SendRequested;
    public event EventHandler? EscapeRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsWebAllowedProperty)
        {
            if (IsWebAllowed)
                WebViewSupport.AfterTransition(this, UpdateVisibility);
            else
                UpdateVisibility();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ThemeService is not null)
            ThemeService.PaletteChanged += OnPaletteChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ThemeService is not null)
            ThemeService.PaletteChanged -= OnPaletteChanged;
    }

    /// <summary>Troca todo o conteúdo do editor.</summary>
    public void SetContent(string html)
    {
        if (!_ready)
        {
            _pendingContent = html;
            return;
        }
        Run(EditorDocument.Call("setContent", html));
    }

    /// <summary>Foco no início do texto (respostas: escrever acima da citação).</summary>
    public void FocusStart()
    {
        if (!_ready)
        {
            _pendingFocusStart = true;
            return;
        }
        _web?.Focus();
        Run("focusStart()");
    }

    /// <summary>Comando de formatação (bold, italic, insertUnorderedList, blockquote, removeFormat…).</summary>
    public void Exec(string command)
    {
        _web?.Focus();
        Run(EditorDocument.Call("cmd", command));
    }

    /// <summary>Comando com valor (ex.: insertImage com um data:).</summary>
    public void Exec(string command, string value)
    {
        _web?.Focus();
        Run($"cmd({JsonSerializer.Serialize(command)}, {JsonSerializer.Serialize(value)})");
    }

    public void InsertLink(string url)
    {
        _web?.Focus();
        Run(EditorDocument.Call("link", url));
    }

    public void FocusEditor()
    {
        _web?.Focus();
        Run("focusEditor()");
    }

    /// <summary>Espera o editor entregar o conteúdo atual (ele avisa alterações com um pequeno atraso).</summary>
    public async Task FlushAsync()
    {
        if (_web is null || !_ready)
            return;
        var tcs = _flush ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Run("flush()");
        await Task.WhenAny(tcs.Task, Task.Delay(1000));
    }

    private void OnMessage(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return;
        JsonElement m;
        try
        {
            m = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        string Str(string name) => m.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        bool Bool(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        switch (Str("type"))
        {
            case "ready":
                _ready = true;
                UpdateVisibility();
                if (_pendingContent is { } content)
                {
                    _pendingContent = null;
                    SetContent(content);
                }
                if (_pendingFocusStart)
                {
                    _pendingFocusStart = false;
                    FocusStart();
                }
                break;
            case "dirty":
                Dirty?.Invoke(this, EventArgs.Empty);
                break;
            case "content":
                ContentChanged?.Invoke(this, Str("html"));
                CompleteFlush();
                break;
            case "flushed":
                CompleteFlush();
                break;
            case "state":
                FormatChanged?.Invoke(this, new EditorFormatState(
                    Bool("bold"), Bool("italic"), Bool("underline"), Bool("strike"), Bool("ul"), Bool("ol")));
                break;
            case "file":
                try
                {
                    FileDropped?.Invoke(this, (Str("name"), Convert.FromBase64String(Str("data"))));
                }
                catch (FormatException)
                {
                }
                break;
            case "tooLarge":
                FileTooLarge?.Invoke(this, Str("name"));
                break;
            case "send":
                SendRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "escape":
                EscapeRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void CompleteFlush()
    {
        _flush?.TrySetResult();
        _flush = null;
    }

    private void UpdateVisibility()
    {
        if (_web is not null)
            _web.IsVisible = _ready && IsWebAllowed;
    }

    private void Run(string script)
    {
        if (_web is null || !_ready)
            return;
        _ = RunAsync(script);
    }

    private async Task RunAsync(string script)
    {
        try
        {
            await _web!.InvokeScript(script);
        }
        catch (Exception)
        {
            // O WebView pode estar sendo destruído (saindo da tela); nada a fazer.
        }
    }

    private void OnPaletteChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ThemeService is not null)
                Run(EditorDocument.SetColorsScript(ThemeService.CurrentPalette()));
        });

    private string BuildDocument()
    {
        var palette = ThemeService?.CurrentPalette() ?? new ThemePalette("#F6F7FB", "#FFFFFF", "#F1F3F9", "#E3E6EF",
            "#141A33", "#5B6385", "#3D6BFF", false);
        return EditorDocument.Build(palette, ComposeHtml.MailFont, Placeholder);
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        var dataDir = Path.Combine(
            (Application.Current as App)?.Services.GetService<AppPaths>()?.DataDirectory ?? Path.GetTempPath(), "webview");
        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs w:
                w.UserDataFolder = dataDir;
                w.IsInPrivateModeEnabled = true;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs a:
                a.NonPersistentDataStore = true;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs g:
                g.EphemeralDataManager = true;
                g.DisableCache = true;
                break;
        }
    }
}

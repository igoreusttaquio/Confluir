using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Confluir.App.Services;
using Confluir.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Confluir.App.Views;

/// <summary>
/// Corpo do email num WebView nativo (WebView2 / WebKit / WebKitGTK). Toda navegação é bloqueada:
/// links abrem no navegador padrão. Sem WebView disponível, mostra o texto puro.
/// </summary>
public partial class MessageBodyView : UserControl
{
    public static readonly StyledProperty<string?> DocumentProperty =
        AvaloniaProperty.Register<MessageBodyView, string?>(nameof(Document));

    public static readonly StyledProperty<string?> FallbackProperty =
        AvaloniaProperty.Register<MessageBodyView, string?>(nameof(Fallback));

    /// <summary>
    /// Falso enquanto outra tela estiver aberta: o WebView é uma janela nativa por cima do app e
    /// não acompanha opacidade/transições, então precisa ser escondido.
    /// </summary>
    public static readonly StyledProperty<bool> IsWebAllowedProperty =
        AvaloniaProperty.Register<MessageBodyView, bool>(nameof(IsWebAllowed), defaultValue: true);

    private readonly NativeWebView? _web;
    private bool _webReady;
    private string? _shown;

    public MessageBodyView()
    {
        InitializeComponent();
        if (!WebViewSupport.IsAvailable)
            return;

        _web = new NativeWebView { IsVisible = false };
        _web.EnvironmentRequested += OnEnvironmentRequested;
        _web.AdapterCreated += (_, _) =>
        {
            _webReady = true;
            Update();
        };
        _web.NavigationStarted += OnNavigationStarted;
        _web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Request);
        };
        Host.Children.Add(_web);
    }

    public string? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public string? Fallback
    {
        get => GetValue(FallbackProperty);
        set => SetValue(FallbackProperty, value);
    }

    public bool IsWebAllowed
    {
        get => GetValue(IsWebAllowedProperty);
        set => SetValue(IsWebAllowedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty || change.Property == FallbackProperty || change.Property == IsWebAllowedProperty)
            Update();
    }

    private void Update()
    {
        FallbackText.Text = Fallback;
        var useWeb = _web is not null && _webReady && Document is not null;
        FallbackScroll.IsVisible = !useWeb;
        if (_web is null)
            return;
        _web.IsVisible = useWeb && IsWebAllowed;
        if (useWeb && !ReferenceEquals(_shown, Document))
        {
            _shown = Document;
            _web.NavigateToString(Document!, null);
        }
    }

    private void OnNavigationStarted(object? sender, Avalonia.Controls.WebViewNavigationStartingEventArgs e)
    {
        // A única navegação permitida é a do próprio documento carregado por NavigateToString.
        if (e.Request is null || e.Request.Scheme is "about" or "data")
            return;
        e.Cancel = true;
        OpenExternally(e.Request);
    }

    private void OpenExternally(Uri? uri)
    {
        if (uri is null || uri.Scheme is not ("http" or "https" or "mailto"))
            return;
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
    }

    private static void OnEnvironmentRequested(object? sender, Avalonia.Controls.WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        var dataDir = Path.Combine(
            (Application.Current as App)?.Services.GetService<AppPaths>()?.DataDirectory ?? Path.GetTempPath(), "webview");
        // Sem cookies, cache ou armazenamento persistentes: cada email é exibido "do zero".
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

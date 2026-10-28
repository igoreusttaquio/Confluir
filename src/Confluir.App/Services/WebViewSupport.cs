using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Confluir.App.Services;

/// <summary>
/// Verifica se o WebView nativo pode ser criado. Criá-lo sem o motor instalado lança uma exceção assíncrona
/// dentro da biblioteca (que derruba o app), então a checagem acontece antes, e sem motor o leitor usa texto puro.
/// </summary>
public static class WebViewSupport
{
    // Id do runtime "Evergreen" do WebView2.
    private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly Lazy<bool> Engine = new(DetectEngine);

    /// <summary>Verdadeiro se há motor instalado e a thread atual pode hospedá-lo.</summary>
    public static bool IsAvailable =>
        Engine.Value &&
        (!OperatingSystem.IsWindows() || Thread.CurrentThread.GetApartmentState() == ApartmentState.STA);

    // Duração das animações de navegação (MainWindow.axaml), com folga.
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Executa <paramref name="show"/> depois da animação de navegação. O WebView é uma janela nativa: posicionado
    /// no meio do deslizamento ele fica deslocado, porque não acompanha a transformação até o fim.
    /// </summary>
    public static void AfterTransition(Avalonia.Controls.Control owner, Action show) =>
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (Avalonia.Controls.TopLevel.GetTopLevel(owner) is not null)
                show();
        }, TransitionDuration);

    private static bool DetectEngine()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return HasWebView2Runtime();
            if (OperatingSystem.IsLinux())
                return new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" }
                    .Any(lib => NativeLibrary.TryLoad(lib, out _));
            return OperatingSystem.IsMacOS();
        }
        catch (Exception)
        {
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasWebView2Runtime()
    {
        // Instalação por máquina (32/64 bits) ou por usuário; "pv" vazio ou 0.0.0.0 significa removido.
        string[] keys =
        [
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}",
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}",
            $@"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}",
        ];
        return keys.Any(k => Registry.GetValue(k, "pv", null) is string v && v.Length > 0 && v != "0.0.0.0");
    }
}

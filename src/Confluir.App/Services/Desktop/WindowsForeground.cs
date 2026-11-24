using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Confluir.App.Services.Desktop;

/// <summary>
/// Traz a janela para a frente no Windows a partir da bandeja. Quem está em primeiro plano no clique é o Explorer,
/// e o Windows só deixa outro processo tomar a frente em certas condições: a janela era mostrada (ou ficava atrás
/// das outras) sem aparecer. Restaura se estiver minimizada e, se o pedido normal for recusado, une a entrada com a
/// janela da frente por um instante (o caminho usual para ativação pedida pelo usuário).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsForeground
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    public static void Bring(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
        if (SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd)
            return;

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var ownThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ownThread &&
                       AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(ownThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
}

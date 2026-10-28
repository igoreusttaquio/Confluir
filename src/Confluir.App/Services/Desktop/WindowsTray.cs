using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace Confluir.App.Services.Desktop;

/// <summary>
/// Ícone na área de notificação do Windows (Shell_NotifyIcon), numa janela de mensagens própria. Um ícone só faz
/// tudo: menu no botão direito, abrir no clique e notificações (o Windows 10/11 as mostra como toast e as guarda na
/// Central de Notificações). As mensagens dessa janela passam pelo mesmo laço da interface do Avalonia.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTray : IDesktopTray
{
    private const int WM_APP_TRAY = 0x8001;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const int NIN_BALLOONUSERCLICK = 0x0405;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_USER = 0x04, NIIF_LARGE_ICON = 0x20;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, TPM_BOTTOMALIGN = 0x0020;
    private const uint MF_STRING = 0x0000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProc _wndProc; // mantido vivo: o Windows chama de volta enquanto a janela existir
    private readonly IntPtr _hwnd;
    private readonly IntPtr _smallIcon;
    private readonly IntPtr _largeIcon;
    private readonly uint _taskbarCreated;
    private IReadOnlyList<TrayMenuItem> _menu = [];
    private string _toolTip = "Confluir";
    private Action? _balloonClick;
    private bool _disposed;

    public WindowsTray()
    {
        _wndProc = OnMessage;
        var instance = GetModuleHandle(null);
        var className = "ConfluirTray" + Environment.ProcessId;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, className, "Confluir", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        // O Explorer reiniciado recria a área de notificação: o ícone precisa ser adicionado de novo.
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        ExtractIconEx(Environment.ProcessPath ?? "", 0, out _largeIcon, out _smallIcon, 1);
        AddIcon();
    }

    public event EventHandler? Activated;

    public void SetToolTip(string text)
    {
        _toolTip = text.Length > 127 ? text[..127] : text;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items) => _menu = items;

    public void Notify(string title, string body, Action? onClick)
    {
        _balloonClick = onClick;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = body.Length > 255 ? body[..255] : body;
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        data.hBalloonIcon = _largeIcon != IntPtr.Zero ? _largeIcon : _smallIcon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddIcon()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private NOTIFYICONDATA NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _smallIcon,
        szTip = _toolTip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            // Versão 4: o evento vem na palavra baixa de lParam.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONUP:
                    Dispatcher.UIThread.Post(() => Activated?.Invoke(this, EventArgs.Empty));
                    break;
                case WM_CONTEXTMENU or WM_RBUTTONUP:
                    ShowMenu();
                    break;
                case NIN_BALLOONUSERCLICK:
                    var click = _balloonClick;
                    _balloonClick = null;
                    if (click is not null)
                        Dispatcher.UIThread.Post(click);
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
            AddIcon();
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (_menu.Count == 0)
            return;
        var menu = CreatePopupMenu();
        for (var i = 0; i < _menu.Count; i++)
            AppendMenu(menu, MF_STRING, (uint)(i + 1), _menu[i].Text);
        GetCursorPos(out var pt);
        // Sem trazer a janela para a frente, o menu não fecha ao clicar fora (comportamento documentado).
        SetForegroundWindow(_hwnd);
        var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (chosen > 0 && chosen <= _menu.Count)
        {
            var item = _menu[chosen - 1];
            Dispatcher.UIThread.Post(item.Invoke);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var data = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        DestroyWindow(_hwnd);
        if (_smallIcon != IntPtr.Zero)
            DestroyIcon(_smallIcon);
        if (_largeIcon != IntPtr.Zero)
            DestroyIcon(_largeIcon);
    }

    // Win32 -----------------------------------------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint icons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

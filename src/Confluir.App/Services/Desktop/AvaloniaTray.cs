using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;

namespace Confluir.App.Services.Desktop;

/// <summary>
/// Ícone na bandeja do Linux (StatusNotifierItem) e na barra de menus do macOS pelo <see cref="TrayIcon"/> do Avalonia.
/// Notificações: <c>notify-send</c> no Linux (libnotify) e a Central de Notificações no macOS (<c>osascript</c>).
/// Nesses sistemas o clique na notificação não volta para o app.
/// </summary>
internal sealed class AvaloniaTray : IDesktopTray
{
    private readonly TrayIcon _tray;
    private readonly ILogger _logger;

    public AvaloniaTray(ILogger logger)
    {
        _logger = logger;
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Confluir/Assets/confluir.ico"))),
            ToolTipText = "Confluir",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        if (Application.Current is { } app)
            TrayIcon.SetIcons(app, [_tray]);
    }

    public event EventHandler? Activated;

    public void SetToolTip(string text) => _tray.ToolTipText = text;

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            var native = new NativeMenuItem(item.Text);
            native.Click += (_, _) => item.Invoke();
            menu.Items.Add(native);
        }
        _tray.Menu = menu;
    }

    public void Notify(string title, string body, Action? onClick)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // Texto do usuário vai como argumento do AppleScript, nunca como código.
                Start("osascript", "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                    "-e", "end run", title, body);
            }
            else if (OperatingSystem.IsLinux())
            {
                Start("notify-send", "--app-name=Confluir", "--icon=confluir", title, body);
            }
        }
        catch (Exception ex)
        {
            // Sem notify-send (ou sem servidor de notificações): o ícone continua mostrando as não lidas.
            _logger.LogInformation(ex, "Notificação do sistema indisponível");
        }
    }

    private static void Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
    }

    public void Dispose()
    {
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}

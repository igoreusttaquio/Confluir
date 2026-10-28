using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Confluir.App.Services.Desktop;

/// <summary>
/// "Iniciar com o sistema": Windows pela chave Run do usuário, Linux por um .desktop em ~/.config/autostart e
/// macOS por um LaunchAgent. O app abre com <see cref="MinimizedArgument"/> e fica só na bandeja.
/// </summary>
public sealed class AutostartService(ILogger<AutostartService> logger)
{
    public const string MinimizedArgument = "--minimizado";
    private const string Name = "Confluir";
    private const string MacLabel = "io.github.igoreusttaquio.confluir";

    public void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;
        try
        {
            if (OperatingSystem.IsWindows())
                ApplyWindows(enabled, exe);
            else if (OperatingSystem.IsLinux())
                ApplyLinux(enabled, exe);
            else if (OperatingSystem.IsMacOS())
                ApplyMac(enabled, exe);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível {Action} o início com o sistema", enabled ? "ativar" : "desativar");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled, string exe)
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            run.SetValue(Name, $"\"{exe}\" {MinimizedArgument}");
        else
            run.DeleteValue(Name, throwOnMissingValue: false);
    }

    private static void ApplyLinux(bool enabled, string exe)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
        var file = Path.Combine(dir, "confluir.desktop");
        if (!enabled)
        {
            File.Delete(file);
            return;
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, $"""
            [Desktop Entry]
            Type=Application
            Name=Confluir
            Exec="{exe}" {MinimizedArgument}
            Icon=confluir
            X-GNOME-Autostart-enabled=true
            """);
    }

    private static void ApplyMac(bool enabled, string exe)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        var file = Path.Combine(dir, $"{MacLabel}.plist");
        if (!enabled)
        {
            File.Delete(file);
            return;
        }
        Directory.CreateDirectory(dir);
        var escaped = System.Security.SecurityElement.Escape(exe);
        File.WriteAllText(file, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{MacLabel}</string>
              <key>ProgramArguments</key><array><string>{escaped}</string><string>{MinimizedArgument}</string></array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }
}

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Confluir.App.Services;

public interface IUrlLauncher
{
    Task OpenAsync(string url);
}

/// <summary>Abre links no navegador padrão, nunca dentro do app.</summary>
public sealed class UrlLauncher : IUrlLauncher
{
    public async Task OpenAsync(string url)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            await window.Launcher.LaunchUriAsync(uri);
    }
}

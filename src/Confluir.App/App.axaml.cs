using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Confluir.App.Services;
using Confluir.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Confluir.App;

public partial class App : Application
{
    public App(IServiceProvider services) => Services = services;

    public IServiceProvider Services { get; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services.GetRequiredService<ThemeService>().ApplyAll();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();

        base.OnFrameworkInitializationCompleted();
    }
}

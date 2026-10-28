using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Confluir.App.Localization;
using Confluir.App.Services;
using Confluir.App.Services.Desktop;
using Confluir.App.ViewModels;
using Confluir.Core.Settings;
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
        // Idioma antes de criar qualquer View, para que tudo já nasça traduzido.
        var settings = Services.GetRequiredService<ISettingsStore>();
        Services.GetRequiredService<LocalizationService>().Apply(settings.Current.Appearance.Language);
        Services.GetRequiredService<ThemeService>().ApplyAll();
        Services.GetRequiredService<FontService>().Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = Services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;
            // Bandeja, notificações e fechar para a bandeja; aberto pelo início com o sistema, fica só na bandeja.
            var startMinimized = desktop.Args?.Contains(AutostartService.MinimizedArgument) == true;
            Services.GetRequiredService<DesktopIntegration>().Attach(desktop, window,
                Services.GetRequiredService<MainWindowViewModel>(), startMinimized);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

using Confluir.App.DesignData;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.App.Views;
using Confluir.Core;
using Confluir.Core.Services;
using Confluir.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Confluir.App;

public static class AppHost
{
    /// <param name="dataDirectory">Pasta de dados alternativa (testes e capturas); nulo usa &lt;AppData&gt;/Confluir.</param>
    public static IHost Build(string? dataDirectory = null)
    {
        var paths = new AppPaths(dataDirectory);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });

        // Nunca registrar senhas, tokens ou conteúdo de emails nos logs.
        builder.Services.AddSerilog(log => log
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "confluir-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14));

        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(paths.SettingsFile));
        builder.Services.AddSingleton<IMailboxService, FakeMailboxService>();
        builder.Services.AddSingleton<ThemeService>();

        builder.Services.AddSingleton<AppearanceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddTransient<MainWindow>();

        return builder.Build();
    }
}

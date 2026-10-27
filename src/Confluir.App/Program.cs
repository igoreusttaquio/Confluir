using System.Globalization;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Confluir.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A interface é em pt-BR; datas e números seguem a mesma cultura.
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        using var host = AppHost.Build();
        var logger = host.Services.GetRequiredService<ILogger<App>>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Exceção não tratada");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Exceção de tarefa não observada");
            e.SetObserved();
        };

        host.Start();
        try
        {
            logger.LogInformation("Confluir iniciado");
            return BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .LogToTrace();

    // Usado pelo previewer do XAML.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(AppHost.Build().Services);
}

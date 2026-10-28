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
        // O idioma (e a cultura de datas e números) é aplicado pelo LocalizationService ao iniciar o App.
        // --dados-falsos: interface com contas e mensagens de exemplo, sem tocar em servidores.
        // --pasta-dados=<pasta>: outra pasta de dados (testes), em vez de <AppData>/Confluir.
        var useFakeData = args.Contains("--dados-falsos");
        var dataDirectory = args.FirstOrDefault(a => a.StartsWith("--pasta-dados=", StringComparison.Ordinal))?["--pasta-dados=".Length..];
        var host = AppHost.Build(dataDirectory, useFakeData);
        var logger = host.Services.GetRequiredService<ILogger<App>>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Exceção não tratada");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Exceção de tarefa não observada");
            e.SetObserved();
        };

        host.Start();
        var exitCode = 0;
        try
        {
            logger.LogInformation("Confluir iniciado");
            exitCode = BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Shutdown(host, logger);
        }
        // Garante o fim do processo: sem isso, algo preso (conexão, thread) deixava o Confluir rodando sem
        // janela, e o instalador da atualização não conseguia fechá-lo.
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Para os serviços (sincronização, IMAP) e grava o log, fora da thread da interface: o laço dela já terminou,
    /// e uma continuação que tentasse voltar para ela esperaria para sempre. Com prazo, para nunca travar a saída.
    /// </summary>
    private static void Shutdown(IHost host, ILogger logger)
    {
        var done = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(StopTimeout);
            try
            {
                await host.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao parar os serviços");
            }
            logger.LogInformation("Confluir encerrado");
            // Por último: descarta o provedor, que grava e fecha o log.
            if (host is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
            else
                host.Dispose();
        }).Wait(StopTimeout + TimeSpan.FromSeconds(3));
        if (!done)
            System.Diagnostics.Trace.WriteLine("Confluir: encerramento passou do prazo; saindo assim mesmo");
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .LogToTrace();

    // Usado pelo previewer do XAML.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(AppHost.Build(useFakeData: true).Services);
}

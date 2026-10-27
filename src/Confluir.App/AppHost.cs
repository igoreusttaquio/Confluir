using Confluir.App.DesignData;
using Confluir.App.Localization;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.App.Views;
using Confluir.Auth;
using Confluir.Core;
using Confluir.Core.Services;
using Confluir.Core.Settings;
using Confluir.Data;
using Confluir.Mail;
using Confluir.Mail.Autoconfig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Confluir.App;

public static class AppHost
{
    /// <param name="dataDirectory">Pasta de dados alternativa (testes e capturas); nulo usa &lt;AppData&gt;/Confluir.</param>
    /// <param name="useFakeData">Usa contas e mensagens falsas em vez dos servidores IMAP.</param>
    public static IHost Build(string? dataDirectory = null, bool useFakeData = false)
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
        builder.Services.AddSingleton(_ => new Database(paths.DatabaseFile));
        builder.Services.AddSingleton<IMailCache, SqliteMailCache>();
        builder.Services.AddSingleton<IAccountStore>(sp =>
            new SqliteAccountStore(sp.GetRequiredService<Database>(), legacyJsonPath: paths.AccountsFile));
        builder.Services.AddSingleton<ICredentialStore>(_ => new SystemCredentialStore(paths.DataDirectory));
        builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

        builder.Services.AddSingleton<IConnectionTester, ConnectionTester>();
        builder.Services.AddSingleton<IServerSettingsResolver, ServerSettingsResolver>();
        builder.Services.AddSingleton<ImapConnectionPool>();
        builder.Services.AddSingleton<ImapSyncEngine>();
        if (useFakeData)
        {
            builder.Services.AddSingleton<IMailboxService, FakeMailboxService>();
        }
        else
        {
            // Lê do cache e sincroniza em segundo plano enquanto o host estiver rodando.
            builder.Services.AddSingleton<SyncingMailboxService>();
            builder.Services.AddSingleton<IMailboxService>(sp => sp.GetRequiredService<SyncingMailboxService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncingMailboxService>());
        }

        builder.Services.AddSingleton(LocalizationService.Instance);
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<NavigationService>();
        builder.Services.AddSingleton<IUrlLauncher, UrlLauncher>();
        builder.Services.AddSingleton<AccountService>();
        builder.Services.AddSingleton<AttachmentService>();
        builder.Services.AddSingleton<ReadingServices>();

        builder.Services.AddSingleton<AppearanceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddTransient<AccountEditorViewModel>();
        builder.Services.AddSingleton<Func<AccountEditorViewModel>>(sp => sp.GetRequiredService<AccountEditorViewModel>);
        builder.Services.AddTransient<MainWindow>();

        return builder.Build();
    }
}

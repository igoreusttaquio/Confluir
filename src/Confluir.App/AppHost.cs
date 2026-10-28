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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        // Configuração (ex.: Microsoft:ClientId): appsettings.json ao lado do executável, depois o da pasta de
        // dados (sobrevive a atualizações) e por fim variáveis de ambiente CONFLUIR_* (ex.: CONFLUIR_Microsoft__ClientId).
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(paths.DataDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables("CONFLUIR_");
        var microsoft = builder.Configuration.GetSection("Microsoft").Get<MicrosoftAuthOptions>() ?? new MicrosoftAuthOptions();
        // Normal: o ID vem embutido na compilação; appsettings/variável de ambiente só sobrescrevem (desenvolvimento).
        if (string.IsNullOrWhiteSpace(microsoft.ClientId))
            microsoft.ClientId = BuiltInMicrosoftClientId();
        builder.Services.AddSingleton<IOAuthTokenProvider>(sp => new MicrosoftOAuthProvider(
            microsoft, paths.DataDirectory, sp.GetRequiredService<ILogger<MicrosoftOAuthProvider>>()));
        builder.Services.AddSingleton<MailAuthenticator>();

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
            builder.Services.AddSingleton<IMailSender, FakeMailSender>();
            builder.Services.AddSingleton<IDraftStore, InMemoryDraftStore>();
        }
        else
        {
            builder.Services.AddSingleton<IMailSender, SmtpMailSender>();
            builder.Services.AddSingleton<IDraftStore, SqliteDraftStore>();
            // Lê do cache e sincroniza em segundo plano enquanto o host estiver rodando.
            builder.Services.AddSingleton<SyncingMailboxService>();
            builder.Services.AddSingleton<IMailboxService>(sp => sp.GetRequiredService<SyncingMailboxService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncingMailboxService>());
        }

        builder.Services.AddSingleton(LocalizationService.Instance);
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<FontService>();
        builder.Services.AddSingleton<NavigationService>();
        builder.Services.AddSingleton<IUrlLauncher, UrlLauncher>();
        builder.Services.AddSingleton<AccountService>();
        builder.Services.AddSingleton<AttachmentService>();
        builder.Services.AddSingleton<ReadingServices>();
        builder.Services.AddSingleton<DraftService>();
        builder.Services.AddSingleton<ComposeServices>();
        builder.Services.AddSingleton<Services.Desktop.DesktopIntegration>();
        builder.Services.AddSingleton<Services.Desktop.AutostartService>();
        builder.Services.AddSingleton<RuleService>();
        builder.Services.AddSingleton<ShortcutService>();
        // Com dados falsos (capturas de tela, testes) não procura atualizações na internet.
        builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<UpdateService>(sp, !useFakeData));
        builder.Services.AddSingleton<RuleEditorServices>();
        builder.Services.AddSingleton<SettingsServices>();

        builder.Services.AddSingleton<AppearanceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddTransient<AccountEditorViewModel>();
        builder.Services.AddSingleton<Func<AccountEditorViewModel>>(sp => sp.GetRequiredService<AccountEditorViewModel>);
        builder.Services.AddTransient<MainWindow>();

        return builder.Build();
    }

    /// <summary>ID do app no Entra ID gravado na compilação (propriedade ConfluirMicrosoftClientId); nulo se não houver.</summary>
    public static string? BuiltInMicrosoftClientId() =>
        typeof(AppHost).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MicrosoftClientId" && !string.IsNullOrWhiteSpace(a.Value))?.Value;
}

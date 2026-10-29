using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;

namespace Confluir.App.ViewModels;

/// <param name="Packages">Pacotes NuGet cobertos por este crédito (o teste confere que todo pacote do app tem crédito).</param>
public sealed record Credit(string Name, string Author, string License, string Url, params string[] Packages);

public sealed record CreditGroup(string TitleKey, IReadOnlyList<Credit> Items);

/// <summary>Tela "Sobre": versão, links e os créditos de bibliotecas, fontes e ícones usados pelo Confluir.</summary>
public sealed partial class AboutViewModel(IUrlLauncher urls, UpdateService updates, Action goBack) : ViewModelBase, INavigationPage
{
    public const string SiteUrl = "https://igoreusttaquio.github.io/Confluir/";
    public const string SourceUrl = "https://github.com/" + UpdateService.Repository;
    public const string PrivacyUrl = SiteUrl + "privacidade.html";

    public string VersionText => string.Format(Strings.Update_Current, updates.CurrentVersionText);

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public IRelayCommand BackCommand { get; } = new RelayCommand(goBack);

    [RelayCommand]
    private Task Open(string url) => urls.OpenAsync(url);

    public IReadOnlyList<CreditGroup> Groups => Credits;

    /// <summary>Licenças conferidas nos pacotes (nuspec) e nos repositórios de cada projeto.</summary>
    public static IReadOnlyList<CreditGroup> Credits { get; } =
    [
        new(nameof(Strings.About_Group_Interface),
        [
            new("Avalonia", "Avalonia Team", "MIT", "https://avaloniaui.net/",
                "Avalonia", "Avalonia.Desktop", "Avalonia.Controls.ColorPicker"),
            new("Avalonia WebView", "AvaloniaUI OÜ", "MIT", "https://github.com/AvaloniaUI/Avalonia", "Avalonia.Controls.WebView"),
            new("FluentAvalonia", "FluentAvalonia", "MIT", "https://github.com/amwx/FluentAvalonia", "FluentAvaloniaUI"),
            new("CommunityToolkit.Mvvm", "Microsoft · .NET Foundation", "MIT", "https://github.com/CommunityToolkit/dotnet",
                "CommunityToolkit.Mvvm"),
            new("SkiaSharp · HarfBuzzSharp", "Microsoft", "MIT", "https://github.com/mono/SkiaSharp"),
            new("Skia", "Google", "BSD-3-Clause", "https://skia.org/"),
            new("HarfBuzz", "HarfBuzz", "MIT", "https://harfbuzz.github.io/"),
            new("Tmds.DBus", "Tom Deseyn", "MIT", "https://github.com/tmds/Tmds.DBus"),
        ]),
        new(nameof(Strings.About_Group_Email),
        [
            new("MailKit", "Jeffrey Stedfast", "MIT", "https://github.com/jstedfast/MailKit", "MailKit"),
            new("MimeKit", "Jeffrey Stedfast", "MIT", "https://github.com/jstedfast/MimeKit", "MimeKit"),
            new("HtmlSanitizer", "Michael Ganss", "MIT", "https://github.com/mganss/HtmlSanitizer", "HtmlSanitizer"),
            new("AngleSharp", "AngleSharp", "MIT", "https://anglesharp.github.io/"),
            new("Bouncy Castle", "Legion of the Bouncy Castle", "MIT", "https://www.bouncycastle.org/"),
        ]),
        new(nameof(Strings.About_Group_Data),
        [
            new("SQLite", "D. Richard Hipp et al.", "Public Domain", "https://sqlite.org/"),
            new("Microsoft.Data.Sqlite", "Microsoft · .NET Foundation", "MIT", "https://github.com/dotnet/efcore",
                "Microsoft.Data.Sqlite"),
            new("SQLitePCLRaw", "Eric Sink", "Apache-2.0", "https://github.com/ericsink/SQLitePCL.raw"),
            new("Dapper", "Sam Saffron, Marc Gravell, Nick Craver", "Apache-2.0", "https://github.com/DapperLib/Dapper", "Dapper"),
            new("MSAL.NET", "Microsoft", "MIT", "https://github.com/AzureAD/microsoft-authentication-library-for-dotnet",
                "Microsoft.Identity.Client.Extensions.Msal"),
        ]),
        new(nameof(Strings.About_Group_Platform),
        [
            new(".NET", "Microsoft · .NET Foundation", "MIT", "https://dotnet.microsoft.com/"),
            new("Microsoft.Extensions", "Microsoft · .NET Foundation", "MIT", "https://github.com/dotnet/runtime",
                "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.Extensions.Logging.Abstractions"),
            new("Serilog", "Serilog Contributors", "Apache-2.0", "https://serilog.net/", "Serilog.Extensions.Hosting", "Serilog.Sinks.File"),
        ]),
        new(nameof(Strings.About_Group_Fonts),
        [
            new("Inter", "Rasmus Andersson", "SIL OFL 1.1", "https://rsms.me/inter/"),
            new("Source Serif 4", "Adobe", "SIL OFL 1.1", "https://github.com/adobe-fonts/source-serif"),
            new("JetBrains Mono", "JetBrains", "SIL OFL 1.1", "https://www.jetbrains.com/lp/mono/"),
            new("Lucide", "Lucide Contributors", "ISC", "https://lucide.dev/"),
            new("Lucide.Avalonia", "Compunet", "MIT", "https://www.nuget.org/packages/Lucide.Avalonia", "Lucide.Avalonia"),
        ]),
    ];
}

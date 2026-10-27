using System.Globalization;

namespace Confluir.Core.Localization;

public sealed record ResolvedLanguage(string Code, CultureInfo UiCulture, CultureInfo FormatCulture);

/// <summary>Decide o idioma da interface e a cultura de formatação (datas, números).</summary>
public static class LanguageResolver
{
    /// <summary>Idiomas com tradução. "pt-BR" é o idioma neutro dos recursos.</summary>
    public static readonly IReadOnlyList<string> Supported = ["pt-BR", "en", "es"];

    public const string Fallback = "en";

    // Cultura de formatação quando o sistema usa outro idioma.
    private static readonly Dictionary<string, string> DefaultFormatCulture = new()
    {
        ["pt-BR"] = "pt-BR",
        ["en"] = "en-US",
        ["es"] = "es-ES",
    };

    /// <param name="setting">Código salvo nas configurações; nulo ou vazio segue o sistema.</param>
    /// <param name="system">Cultura de interface do sistema operacional.</param>
    public static ResolvedLanguage Resolve(string? setting, CultureInfo system)
    {
        var code = Supported.FirstOrDefault(s => string.Equals(s, setting, StringComparison.OrdinalIgnoreCase))
                   ?? FromCulture(system);

        // Se o sistema usa uma variante do mesmo idioma (ex.: en-GB, es-MX), mantém a formatação dela.
        var language = code[..2];
        var format = !system.IsNeutralCulture && system.TwoLetterISOLanguageName == language
            ? system
            : CultureInfo.GetCultureInfo(DefaultFormatCulture[code]);

        return new ResolvedLanguage(code, CultureInfo.GetCultureInfo(code), format);
    }

    private static string FromCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "pt" => "pt-BR",
        "en" => "en",
        "es" => "es",
        _ => Fallback,
    };
}

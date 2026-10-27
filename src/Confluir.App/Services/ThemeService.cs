using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Confluir.Core.Settings;
using Confluir.Core.Theming;
using FluentAvalonia.Styling;

namespace Confluir.App.Services;

/// <summary>Aplica tema (claro/escuro/sistema) e cor de destaque em tempo de execução, sem reiniciar.</summary>
public sealed class ThemeService(ISettingsStore settings)
{
    /// <summary>Accent padrão do tema claro; o FluentAvalonia deriva dele a rampa usada pelos controles.</summary>
    public const string DefaultAccent = "#2F5BEA";

    private const string OnAccentLight = "#FFFFFF";
    private const string OnAccentDark = "#0B1020";

    private static readonly string[] AccentKeys = ["Accent", "OnAccent", "AccentSubtle"];

    private AppearanceSettings Appearance => settings.Current.Appearance;

    public ThemeMode Theme => Appearance.Theme;

    public string? AccentColor => Appearance.AccentColor;

    public void ApplyAll()
    {
        ApplyTheme();
        ApplyAccent();
    }

    public void SetTheme(ThemeMode mode)
    {
        Appearance.Theme = mode;
        ApplyTheme();
        settings.Save();
    }

    /// <param name="hex">#RRGGBB, ou nulo para voltar ao padrão de cada tema.</param>
    public void SetAccent(string? hex)
    {
        Appearance.AccentColor = ColorContrast.IsValidHex(hex) ? hex : null;
        ApplyAccent();
        settings.Save();
    }

    private void ApplyTheme()
    {
        if (Application.Current is not { } app)
            return;
        app.RequestedThemeVariant = Appearance.Theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void ApplyAccent()
    {
        if (Application.Current is not { } app)
            return;

        var custom = Appearance.AccentColor;
        var faTheme = app.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();

        if (custom is null || !ColorContrast.IsValidHex(custom))
        {
            // Sem cor personalizada: os dicionários Light/Dark fornecem os tokens.
            foreach (var key in AccentKeys)
                app.Resources.Remove(key);
            faTheme?.CustomAccentColor = Color.Parse(DefaultAccent);
            return;
        }

        // Chaves no dicionário raiz têm precedência sobre os dicionários de tema, valendo nos dois temas.
        var accent = Color.Parse(custom);
        app.Resources["Accent"] = new SolidColorBrush(accent);
        app.Resources["OnAccent"] = new SolidColorBrush(Color.Parse(
            ColorContrast.PickForeground(custom, OnAccentLight, OnAccentDark)));
        app.Resources["AccentSubtle"] = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
        faTheme?.CustomAccentColor = accent;
    }
}

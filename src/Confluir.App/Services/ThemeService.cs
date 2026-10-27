using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Confluir.Core.Settings;
using Confluir.Core.Theming;
using FluentAvalonia.Styling;

namespace Confluir.App.Services;

/// <summary>Cores do tema atual em hexadecimal, para conteúdo fora do Avalonia (ex.: HTML dos emails).</summary>
public sealed record ThemePalette(
    string Background, string Surface, string SurfaceAlt, string Border,
    string TextPrimary, string TextSecondary, string Accent, bool IsDark);

/// <summary>Aplica tema (claro/escuro/sistema) e cor de destaque em tempo de execução, sem reiniciar.</summary>
public sealed class ThemeService(ISettingsStore settings)
{
    private bool _watching;

    /// <summary>O tema efetivo (inclusive pelo sistema) ou a cor de destaque mudou.</summary>
    public event EventHandler? PaletteChanged;

    public ThemePalette CurrentPalette()
    {
        var app = Application.Current;
        var variant = app?.ActualThemeVariant ?? ThemeVariant.Light;
        string Hex(string key, string fallback) =>
            app is not null && app.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush
                ? $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}"
                : fallback;

        return new ThemePalette(
            Hex("Background", "#F6F7FB"), Hex("Surface", "#FFFFFF"), Hex("SurfaceAlt", "#EEF1FA"), Hex("Border", "#DDE2F0"),
            Hex("TextPrimary", "#141A33"), Hex("TextSecondary", "#5B6385"), Hex("Accent", "#2F5BEA"),
            variant == ThemeVariant.Dark);
    }
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
        if (!_watching && Application.Current is { } app)
        {
            // Inclui a troca pelo sistema operacional quando o tema é "seguir o sistema".
            app.ActualThemeVariantChanged += (_, _) => PaletteChanged?.Invoke(this, EventArgs.Empty);
            _watching = true;
        }
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
        PaletteChanged?.Invoke(this, EventArgs.Empty);
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

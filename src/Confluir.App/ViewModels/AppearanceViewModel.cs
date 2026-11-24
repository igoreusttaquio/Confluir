using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Confluir.App.Localization;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Settings;

namespace Confluir.App.ViewModels;

public sealed record AccentOption(string? Hex, string NameKey)
{
    /// <summary>Cor exibida na amostra; a opção padrão mostra o Accent do tema claro.</summary>
    public string SwatchHex => Hex ?? ThemeService.DefaultAccent;
}

/// <param name="Code">"pt-BR", "en", "es" ou nulo para seguir o sistema.</param>
public sealed record LanguageOption(string? Code, string LabelKey);

/// <summary>Tema, cor de destaque, densidade e idioma. As mudanças se aplicam e são salvas na hora.</summary>
public sealed partial class AppearanceViewModel : ViewModelBase
{
    private readonly ThemeService _theme;
    private readonly ISettingsStore _settings;
    private readonly LocalizationService _loc;
    private bool _syncing;

    public AppearanceViewModel(ThemeService theme, ISettingsStore settings, LocalizationService localization)
    {
        _theme = theme;
        _settings = settings;
        _loc = localization;

        LanguageOptions =
        [
            new LanguageOption(null, nameof(Strings.Language_System)),
            new LanguageOption("pt-BR", nameof(Strings.Language_PtBR)),
            new LanguageOption("en", nameof(Strings.Language_En)),
            new LanguageOption("es", nameof(Strings.Language_Es)),
        ];
        _syncing = true;
        SelectedLanguage = LanguageOptions.FirstOrDefault(o => o.Code == localization.Setting) ?? LanguageOptions[0];
        _syncing = false;

        AccentOptions =
        [
            new AccentOption(null, nameof(Strings.Accent_Default)),
            // As cores de conta servem também como predefinições de destaque.
            .. AccountColors.All.Select(c => new AccentOption(c.Hex, ColorNames.KeyFor(c.Key))),
        ];

        _syncing = true;
        SelectedAccent = AccentOptions.FirstOrDefault(o =>
            string.Equals(o.Hex, theme.AccentColor, StringComparison.OrdinalIgnoreCase));
        CustomColor = Color.Parse(theme.AccentColor ?? ThemeService.DefaultAccent);
        CompactMode = settings.Current.Appearance.CompactMode;
        _syncing = false;
    }

    public IReadOnlyList<AccentOption> AccentOptions { get; }

    public bool IsListTextSmall
    {
        get => _theme.ListTextSize == ListTextSize.Small;
        set { if (value) SetListTextSize(ListTextSize.Small); }
    }

    public bool IsListTextMedium
    {
        get => _theme.ListTextSize == ListTextSize.Medium;
        set { if (value) SetListTextSize(ListTextSize.Medium); }
    }

    public bool IsListTextLarge
    {
        get => _theme.ListTextSize == ListTextSize.Large;
        set { if (value) SetListTextSize(ListTextSize.Large); }
    }

    private void SetListTextSize(ListTextSize size)
    {
        if (_theme.ListTextSize == size)
            return;
        _theme.SetListTextSize(size);
        OnPropertyChanged(nameof(IsListTextSmall));
        OnPropertyChanged(nameof(IsListTextMedium));
        OnPropertyChanged(nameof(IsListTextLarge));
    }

    /// <summary>O layout (duas/três colunas) mudou.</summary>
    public event EventHandler? LayoutChanged;

    public bool IsThreeColumns
    {
        get => _settings.Current.Layout.ReadingLayout == ReadingLayout.ThreeColumns;
        set { if (value) SetLayout(ReadingLayout.ThreeColumns); }
    }

    public bool IsTwoColumns
    {
        get => _settings.Current.Layout.ReadingLayout == ReadingLayout.TwoColumns;
        set { if (value) SetLayout(ReadingLayout.TwoColumns); }
    }

    private void SetLayout(ReadingLayout layout)
    {
        if (_settings.Current.Layout.ReadingLayout == layout)
            return;
        _settings.Current.Layout.ReadingLayout = layout;
        _settings.Save();
        OnPropertyChanged(nameof(IsThreeColumns));
        OnPropertyChanged(nameof(IsTwoColumns));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (_syncing || value is null)
            return;
        _settings.Current.Appearance.Language = value.Code;
        _settings.Save();
        _loc.Apply(value.Code);
    }

    public bool IsLight
    {
        get => _theme.Theme == ThemeMode.Light;
        set { if (value) SetTheme(ThemeMode.Light); }
    }

    public bool IsDark
    {
        get => _theme.Theme == ThemeMode.Dark;
        set { if (value) SetTheme(ThemeMode.Dark); }
    }

    public bool IsSystem
    {
        get => _theme.Theme == ThemeMode.System;
        set { if (value) SetTheme(ThemeMode.System); }
    }

    /// <summary>Predefinição selecionada; nulo quando a cor atual é personalizada.</summary>
    [ObservableProperty]
    public partial AccentOption? SelectedAccent { get; set; }

    [ObservableProperty]
    public partial Color CustomColor { get; set; }

    [ObservableProperty]
    public partial bool CompactMode { get; set; }

    partial void OnSelectedAccentChanged(AccentOption? value)
    {
        if (_syncing || value is null)
            return;
        _theme.SetAccent(value.Hex);
        Sync(() => CustomColor = Color.Parse(value.SwatchHex));
    }

    partial void OnCustomColorChanged(Color value)
    {
        if (_syncing)
            return;
        var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        _theme.SetAccent(hex);
        Sync(() => SelectedAccent = AccentOptions.FirstOrDefault(o =>
            string.Equals(o.Hex, hex, StringComparison.OrdinalIgnoreCase)));
    }

    partial void OnCompactModeChanged(bool value)
    {
        if (_syncing)
            return;
        _settings.Current.Appearance.CompactMode = value;
        _settings.Save();
    }

    private void SetTheme(ThemeMode mode)
    {
        if (_theme.Theme == mode)
            return;
        _theme.SetTheme(mode);
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(IsSystem));
    }

    private void Sync(Action action)
    {
        _syncing = true;
        try { action(); }
        finally { _syncing = false; }
    }

}

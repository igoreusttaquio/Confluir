using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Settings;

namespace Confluir.App.ViewModels;

public sealed record AccentOption(string? Hex, string Name)
{
    /// <summary>Cor exibida na amostra; a opção padrão mostra o Accent do tema claro.</summary>
    public string SwatchHex => Hex ?? ThemeService.DefaultAccent;
}

/// <summary>Tema, cor de destaque e densidade. As mudanças se aplicam e são salvas na hora.</summary>
public sealed partial class AppearanceViewModel : ViewModelBase
{
    private readonly ThemeService _theme;
    private readonly ISettingsStore _settings;
    private bool _syncing;

    public AppearanceViewModel(ThemeService theme, ISettingsStore settings)
    {
        _theme = theme;
        _settings = settings;

        AccentOptions =
        [
            new AccentOption(null, Strings.Accent_Default),
            // As cores de conta servem também como predefinições de destaque.
            .. AccountColors.All.Select(c => new AccentOption(c.Hex, ColorNames.For(c.Key))),
        ];

        _syncing = true;
        SelectedAccent = AccentOptions.FirstOrDefault(o =>
            string.Equals(o.Hex, theme.AccentColor, StringComparison.OrdinalIgnoreCase));
        CustomColor = Color.Parse(theme.AccentColor ?? ThemeService.DefaultAccent);
        CompactMode = settings.Current.Appearance.CompactMode;
        _syncing = false;
    }

    public IReadOnlyList<AccentOption> AccentOptions { get; }

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

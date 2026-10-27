namespace Confluir.Core.Settings;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Cor de destaque escolhida pelo usuário (#RRGGBB); nulo usa a cor padrão de cada tema.</summary>
    public string? AccentColor { get; set; }

    public bool CompactMode { get; set; }
}

public sealed class LayoutSettings
{
    public double SidebarWidth { get; set; } = 260;
    public double MessageListWidth { get; set; } = 400;
}

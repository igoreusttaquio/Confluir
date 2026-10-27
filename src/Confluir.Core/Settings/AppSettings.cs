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
    public ReadingSettings Reading { get; set; } = new();
}

public sealed class ReadingSettings
{
    /// <summary>Remetentes (endereços em minúsculas) cujas imagens remotas são sempre exibidas.</summary>
    public List<string> TrustedImageSenders { get; set; } = [];
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Cor de destaque escolhida pelo usuário (#RRGGBB); nulo usa a cor padrão de cada tema.</summary>
    public string? AccentColor { get; set; }

    public bool CompactMode { get; set; }

    /// <summary>Idioma da interface ("pt-BR", "en", "es"); nulo segue o sistema.</summary>
    public string? Language { get; set; }
}

public enum ReadingLayout
{
    /// <summary>Barra lateral, lista e painel de leitura.</summary>
    ThreeColumns,

    /// <summary>Barra lateral e lista; a mensagem abre em tela própria.</summary>
    TwoColumns,
}

public sealed class LayoutSettings
{
    public double SidebarWidth { get; set; } = 260;
    public double MessageListWidth { get; set; } = 400;

    /// <summary>Barra lateral reduzida a uma faixa de ícones.</summary>
    public bool SidebarCollapsed { get; set; }

    public ReadingLayout ReadingLayout { get; set; } = ReadingLayout.ThreeColumns;
}

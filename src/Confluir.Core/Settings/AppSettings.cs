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
    public ComposeSettings Compose { get; set; } = new();
    public FontSettings Fonts { get; set; } = new();

    /// <summary>Regras aplicadas às mensagens novas, na ordem da lista.</summary>
public List<Rules.MailRule> Rules { get; set; } = [];

    public NotificationSettings Notifications { get; set; } = new();
    public DesktopSettings Desktop { get; set; } = new();
}

public sealed class NotificationSettings
{
    /// <summary>Notificação do sistema para mensagens novas.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Não perturbe: nenhuma notificação até desligar.</summary>
    public bool DoNotDisturb { get; set; }

    /// <summary>Contas que não notificam.</summary>
    public List<Guid> MutedAccounts { get; set; } = [];
}

public sealed class DesktopSettings
{
    /// <summary>Fechar a janela mantém o Confluir na bandeja (recebendo e notificando).</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Abrir com o sistema (já na bandeja).</summary>
    public bool StartWithSystem { get; set; }
}

public sealed class ComposeSettings
{
    /// <summary>Escrever em texto simples em vez do editor com formatação (lembra a última escolha).</summary>
    public bool PlainText { get; set; }
}

public sealed class ReadingSettings
{
    /// <summary>Remetentes (endereços em minúsculas) cujas imagens remotas são sempre exibidas.</summary>
    public List<string> TrustedImageSenders { get; set; } = [];

    /// <summary>Segundos com a mensagem aberta até marcá-la como lida: 0 na hora, negativo nunca (só manualmente).</summary>
    public int MarkReadDelaySeconds { get; set; }
}

/// <summary>
/// Fontes escolhidas. Cada uma é uma chave "origem:família" (ex.: "system:Segoe UI", "bundled:Inter",
/// "imported:Nunito"); nulo usa o padrão do app.
/// </summary>
public sealed class FontSettings
{
    public string? Interface { get; set; }
    public string? Reading { get; set; }
    public string? Mono { get; set; }

    /// <summary>Escala de toda a interface (1 = 100%).</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Altura de linha do corpo dos emails.</summary>
    public double ReadingLineHeight { get; set; } = 1.6;
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Cor de destaque escolhida pelo usuário (#RRGGBB); nulo usa a cor padrão de cada tema.</summary>
    public string? AccentColor { get; set; }

    public bool CompactMode { get; set; }

    /// <summary>Tamanho do texto na lista de mensagens.</summary>
    public ListTextSize ListTextSize { get; set; } = ListTextSize.Medium;

    /// <summary>Idioma da interface ("pt-BR", "en", "es"); nulo segue o sistema.</summary>
    public string? Language { get; set; }
}

public enum ListTextSize
{
    Small,
    Medium,
    Large,
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

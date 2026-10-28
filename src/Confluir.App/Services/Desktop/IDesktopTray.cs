namespace Confluir.App.Services.Desktop;

/// <summary>Item do menu do ícone na bandeja.</summary>
public sealed record TrayMenuItem(string Text, Action Invoke);

/// <summary>Ícone na bandeja/área de notificação, com menu e notificações do sistema.</summary>
public interface IDesktopTray : IDisposable
{
    /// <summary>Clique (ou Enter) no ícone.</summary>
    event EventHandler? Activated;

    void SetToolTip(string text);

    /// <summary>Atualiza os itens do menu (ex.: troca de idioma).</summary>
    void SetMenu(IReadOnlyList<TrayMenuItem> items);

    /// <summary>Notificação do sistema; <paramref name="onClick"/> roda na thread da interface ao clicar nela.</summary>
    void Notify(string title, string body, Action? onClick);
}

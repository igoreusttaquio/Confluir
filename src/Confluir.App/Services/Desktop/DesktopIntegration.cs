using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Confluir.App.Localization;
using Confluir.App.Resources;
using Confluir.App.ViewModels;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Confluir.App.Services.Desktop;

/// <summary>
/// Integração com o sistema: ícone na bandeja (com as não lidas na dica), notificações de mensagens novas
/// (respeitando "não perturbe", contas silenciadas e a janela em uso), fechar para a bandeja e iniciar minimizado.
/// </summary>
public sealed class DesktopIntegration(
    IMailboxService mailbox,
    ISettingsStore settings,
    LocalizationService localization,
    ILogger<DesktopIntegration> logger) : IDisposable
{
    private IDesktopTray? _tray;
    private Window? _window;
    private MainWindowViewModel? _vm;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private bool _exiting;

    /// <summary>Há ícone na bandeja (sem ele, "fechar para a bandeja" não se aplica).</summary>
    public bool HasTray => _tray is not null;

    public void Attach(IClassicDesktopStyleApplicationLifetime lifetime, Window window, MainWindowViewModel vm, bool startMinimized)
    {
        _lifetime = lifetime;
        _window = window;
        _vm = vm;
        try
        {
            _tray = OperatingSystem.IsWindows() ? new WindowsTray() : new AvaloniaTray(logger);
        }
        catch (Exception ex)
        {
            // Ex.: Linux sem área de notificação. O app funciona normalmente, só sem ícone e notificações.
            logger.LogWarning(ex, "Ícone na bandeja indisponível");
        }

        if (_tray is not null)
        {
            _tray.Activated += (_, _) => ShowWindow();
            UpdateMenu();
            UpdateToolTip();
            localization.LanguageChanged += (_, _) =>
            {
                UpdateMenu();
                UpdateToolTip();
            };
            vm.UnifiedInbox.PropertyChanged += OnUnifiedChanged;
        }

        window.Closing += OnWindowClosing;
        lifetime.Exit += (_, _) => Dispose();
        mailbox.MessagesArrived += (_, e) => Dispatcher.UIThread.Post(() => OnMessagesArrived(e));

        // Aberto pelo início com o sistema: fica só na bandeja.
        if (startMinimized && _tray is not null)
            window.Opened += (_, _) => window.Hide();
    }

    private void OnUnifiedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UnifiedInboxNode.UnreadCount) or "")
            UpdateToolTip();
    }

    private void UpdateToolTip()
    {
        var unread = _vm?.UnifiedInbox.UnreadCount ?? 0;
        _tray?.SetToolTip(unread > 0 ? $"Confluir — {localization.Plural("MessageList_UnreadCount", unread)}" : "Confluir");
    }

    private void UpdateMenu() =>
        _tray?.SetMenu(
        [
            new TrayMenuItem(Strings.Tray_Open, ShowWindow),
            new TrayMenuItem(Strings.Action_NewMessage, () =>
            {
                ShowWindow();
                _vm?.NewMessageCommand.Execute(null);
            }),
            new TrayMenuItem(Strings.Action_Sync, () => _vm?.RefreshCommand.Execute(null)),
            new TrayMenuItem(Strings.Tray_Exit, Exit),
        ]);

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || _tray is null || !settings.Current.Desktop.CloseToTray)
            return;
        e.Cancel = true;
        _window?.Hide();
    }

    public void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Exit()
    {
        _exiting = true;
        _lifetime?.Shutdown();
    }

    private void OnMessagesArrived(MessagesArrivedEventArgs e)
    {
        var options = settings.Current.Notifications;
        if (!options.Enabled || options.DoNotDisturb || options.MutedAccounts.Contains(e.AccountId) || _tray is null)
            return;
        // Com a janela na frente, a lista já mostra as novas.
        if (_window is { IsVisible: true, IsActive: true })
            return;
        var (title, body) = Describe(e.Messages, AccountName(e.AccountId));
        var single = e.Messages.Count == 1 ? e.Messages[0].Id : (Guid?)null;
        _tray.Notify(title, body, () =>
        {
            ShowWindow();
            if (_vm is not null)
                _ = _vm.RevealMessageAsync(single);
        });
    }

    /// <summary>Configurações → Notificações → Testar.</summary>
    public void TestNotification() => _tray?.Notify(Strings.Notify_TestTitle, Strings.Notify_TestBody, ShowWindow);

    private string? AccountName(Guid accountId) =>
        _vm?.AccountNodes.FirstOrDefault(n => n.Account.Id == accountId)?.Account.DisplayName;

    /// <summary>Uma: remetente e assunto. Várias: quantas e de quem.</summary>
    public static (string Title, string Body) Describe(IReadOnlyList<MessageSummary> messages, string? account)
    {
        static string Sender(MessageSummary m) => string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : m.FromName;
        var suffix = string.IsNullOrEmpty(account) ? "" : $"\n{account}";
        if (messages.Count == 1)
        {
            var m = messages[0];
            var subject = string.IsNullOrWhiteSpace(m.Subject) ? Strings.NoSubject : m.Subject;
            return (Sender(m), subject + suffix);
        }
        var senders = messages.Select(Sender).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var names = string.Join(", ", senders.Take(3));
        if (senders.Count > 3)
            names += " " + string.Format(Strings.Notify_AndMore, senders.Count - 3);
        return (string.Format(Strings.Notify_Many, messages.Count), names + suffix);
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
    }
}

using Confluir.App.Resources;
using Confluir.Core.Compose;

namespace Confluir.App.ViewModels;

public enum ShortcutAction
{
    NewMessage,
    Search,
    Reply,
    ReplyAll,
    Forward,
    Archive,
    Delete,
    ToggleRead,
    ToggleFlag,
    Sync,
    Settings,
}

/// <summary>Uma ação com atalho (as teclas ficam no <see cref="Services.ShortcutService"/>).</summary>
public sealed record ShortcutInfo(ShortcutAction Action, string LabelKey)
{
    /// <summary>Atalhos que agem sobre mensagens: não valem com o foco num campo de texto.</summary>
    public static bool ActsOnMessages(ShortcutAction action) =>
        action is ShortcutAction.Reply or ShortcutAction.ReplyAll or ShortcutAction.Forward or ShortcutAction.Archive
            or ShortcutAction.Delete or ShortcutAction.ToggleRead or ShortcutAction.ToggleFlag;

    /// <summary>A lista mostrada em Configurações → Atalhos, nesta ordem.</summary>
    public static IReadOnlyList<ShortcutInfo> All { get; } =
    [
        new(ShortcutAction.NewMessage, nameof(Strings.Action_NewMessage)),
        new(ShortcutAction.Search, nameof(Strings.Shortcut_Search)),
        new(ShortcutAction.Reply, nameof(Strings.Action_Reply)),
        new(ShortcutAction.ReplyAll, nameof(Strings.Action_ReplyAll)),
        new(ShortcutAction.Forward, nameof(Strings.Action_Forward)),
        new(ShortcutAction.Archive, nameof(Strings.Action_Archive)),
        new(ShortcutAction.Delete, nameof(Strings.Action_Delete)),
        new(ShortcutAction.ToggleRead, nameof(Strings.Shortcut_ToggleRead)),
        new(ShortcutAction.ToggleFlag, nameof(Strings.Shortcut_ToggleFlag)),
        new(ShortcutAction.Sync, nameof(Strings.Action_Sync)),
        new(ShortcutAction.Settings, nameof(Strings.Settings)),
    ];

    public static ShortcutInfo For(ShortcutAction action) => All.First(s => s.Action == action);
}

/// <summary>Atalhos de teclado da janela principal.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>A janela foca o campo de busca (a View sabe onde ele está).</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>Mensagem aberta: no painel de leitura ou em tela própria.</summary>
    private MessageDetailViewModel? ActiveDetail =>
        Navigation.CurrentPage is MessagePageViewModel page ? page.Detail
        : Navigation.IsAtRoot ? Detail
        : null;

    /// <summary>Sobre o que as ações agem: a seleção (várias), a mensagem aberta ou a selecionada na lista.</summary>
    private IReadOnlyList<MessageRow> ShortcutTargets =>
        Navigation.IsAtRoot && HasMultipleSelection ? SelectedMessages
        : ActiveDetail is { } detail ? [detail.Row]
        : Navigation.IsAtRoot && SelectedRow is MessageRow row ? [row]
        : [];

    /// <summary>Executa o atalho; falso se não se aplica agora (a tecla segue para quem tiver o foco).</summary>
    public bool RunShortcut(ShortcutAction action)
    {
        var targets = ShortcutTargets;
        switch (action)
        {
            case ShortcutAction.NewMessage:
                NewMessageCommand.Execute(null);
                return true;
            case ShortcutAction.Search:
                if (!Navigation.IsAtRoot)
                    Navigation.GoToRoot();
                FocusSearchRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case ShortcutAction.Sync:
                RefreshCommand.Execute(null);
                return true;
            case ShortcutAction.Settings:
                OpenSettingsCommand.Execute(null);
                return true;
            case ShortcutAction.Reply or ShortcutAction.ReplyAll or ShortcutAction.Forward when targets.Count == 1:
                var mode = action switch
                {
                    ShortcutAction.Reply => ComposeMode.Reply,
                    ShortcutAction.ReplyAll => ComposeMode.ReplyAll,
                    _ => ComposeMode.Forward,
                };
                _ = ComposeForRowAsync(targets[0], mode, ActiveDetail?.Row == targets[0] ? ActiveDetail.LoadedBody : null);
                return true;
            case ShortcutAction.Archive when targets.Count > 0:
                _ = ArchiveRowsAsync(targets);
                return true;
            case ShortcutAction.Delete when targets.Count > 0:
                _ = DeleteRowsAsync(targets);
                return true;
            case ShortcutAction.ToggleRead when targets.Count > 0:
                _ = ToggleReadRowsAsync(targets);
                return true;
            case ShortcutAction.ToggleFlag when targets.Count > 0:
                _ = ToggleFlagRowsAsync(targets);
                return true;
            default:
                return false;
        }
    }
}

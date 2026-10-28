using Avalonia.Input;
using Confluir.App.ViewModels;
using Confluir.Core.Settings;

namespace Confluir.App.Services;

/// <summary>
/// Atalhos de teclado: os padrões do app e os que o usuário trocou (salvos nas configurações). A janela principal
/// pergunta aqui qual ação uma tecla dispara; Configurações → Atalhos altera.
/// </summary>
public sealed class ShortcutService(ISettingsStore settings)
{
    /// <summary>No macOS o modificador dos atalhos é ⌘ (Meta).</summary>
    private static KeyModifiers Mod => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public static IReadOnlyDictionary<ShortcutAction, KeyGesture> Defaults { get; } = new Dictionary<ShortcutAction, KeyGesture>
    {
        [ShortcutAction.NewMessage] = new(Key.N, Mod),
        [ShortcutAction.Search] = new(Key.F, Mod),
        [ShortcutAction.Reply] = new(Key.R, Mod),
        [ShortcutAction.ReplyAll] = new(Key.R, Mod | KeyModifiers.Shift),
        [ShortcutAction.Forward] = new(Key.F, Mod | KeyModifiers.Shift),
        [ShortcutAction.Archive] = new(Key.E, Mod),
        [ShortcutAction.Delete] = new(Key.Delete),
        [ShortcutAction.ToggleRead] = new(Key.Q, Mod),
        [ShortcutAction.ToggleFlag] = new(Key.S, Mod | KeyModifiers.Shift),
        [ShortcutAction.Sync] = new(Key.F5),
        [ShortcutAction.Settings] = new(Key.OemComma, Mod),
    };

    public event EventHandler? Changed;

    private Dictionary<string, string> Custom => settings.Current.Shortcuts;

    public KeyGesture GestureFor(ShortcutAction action)
    {
        if (Custom.TryGetValue(action.ToString(), out var text))
        {
            try
            {
                return KeyGesture.Parse(text);
            }
            catch (Exception)
            {
                // Valor inválido no arquivo: vale o padrão.
            }
        }
        return Defaults[action];
    }

    public bool IsCustom(ShortcutAction action) => Custom.ContainsKey(action.ToString());

    /// <summary>Texto como aparece na lista (ex.: "Ctrl+Shift+R"; no macOS com os símbolos do sistema).</summary>
    public static string Display(KeyGesture gesture) => gesture.ToString("p", null);

    /// <summary>Qual ação esta tecla dispara agora (nula = nenhuma).</summary>
    public ShortcutAction? Find(KeyEventArgs e)
    {
        foreach (var action in Defaults.Keys)
        {
            if (GestureFor(action).Matches(e))
                return action;
        }
        return null;
    }

    /// <summary>Outra ação que já usa esta combinação.</summary>
    public ShortcutAction? ConflictWith(KeyGesture gesture, ShortcutAction except) =>
        Defaults.Keys.Where(a => a != except).Cast<ShortcutAction?>()
            .FirstOrDefault(a => GestureFor(a!.Value).Equals(gesture));

    /// <summary>
    /// Combinações aceitas: com Ctrl/⌘/Alt, ou F1–F12 e Delete sozinhas. Uma letra sozinha dispararia enquanto se digita.
    /// </summary>
    public static bool IsAllowed(KeyGesture gesture) =>
        gesture.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None or Key.Escape) &&
        ((gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) != 0 ||
         gesture.Key is >= Key.F1 and <= Key.F12 or Key.Delete);

    public void Set(ShortcutAction action, KeyGesture gesture)
    {
        Store(action, gesture);
        Save();
    }

    /// <summary>Dá a <paramref name="action"/> a combinação de <paramref name="other"/>, e a ela a antiga.</summary>
    public void Swap(ShortcutAction action, ShortcutAction other)
    {
        var (mine, theirs) = (GestureFor(action), GestureFor(other));
        Store(action, theirs);
        Store(other, mine);
        Save();
    }

    private void Store(ShortcutAction action, KeyGesture gesture)
    {
        if (gesture.Equals(Defaults[action]))
            Custom.Remove(action.ToString());
        else
            Custom[action.ToString()] = gesture.ToString();
    }

    public void Reset(ShortcutAction action)
    {
        Custom.Remove(action.ToString());
        Save();
    }

    public void ResetAll()
    {
        Custom.Clear();
        Save();
    }

    private void Save()
    {
        settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

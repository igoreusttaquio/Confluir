using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;

namespace Confluir.App.ViewModels;

/// <summary>
/// Uma linha de Configurações → Atalhos: "Alterar" grava a próxima combinação pressionada (a View repassa as teclas
/// para <see cref="Capture"/>). Se outra ação já usa a combinação, oferece trocar as duas.
/// </summary>
public sealed partial class ShortcutItemViewModel(ShortcutInfo info, ShortcutService shortcuts, SettingsViewModel owner) : ViewModelBase
{
    private ShortcutAction? _conflict;
    private KeyGesture? _pending;

    public ShortcutInfo Info { get; } = info;

    public string LabelKey => Info.LabelKey;

    public string Keys => ShortcutService.Display(shortcuts.GestureFor(Info.Action));

    public bool IsCustom => shortcuts.IsCustom(Info.Action);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsRecording { get; private set; }

    public bool IsIdle => !IsRecording;

    /// <summary>Aviso embaixo da linha (combinação não aceita ou já usada).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    public partial bool CanSwap { get; private set; }

    [RelayCommand]
    private void Change()
    {
        owner.StartRecording(this);
        Clear();
        IsRecording = true;
    }

    /// <summary>Tecla pressionada enquanto grava. Esc cancela; modificadores sozinhos esperam a tecla principal.</summary>
    public void Capture(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Escape)
        {
            Stop();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None)
            return;

        var gesture = new KeyGesture(key, modifiers);
        if (!ShortcutService.IsAllowed(gesture))
        {
            Clear();
            Message = Strings.Shortcut_NeedsModifier;
            return;
        }
        if (shortcuts.ConflictWith(gesture, Info.Action) is { } other)
        {
            _conflict = other;
            _pending = gesture;
            var label = Strings.ResourceManager.GetString(ShortcutInfo.For(other).LabelKey, Strings.Culture) ?? other.ToString();
            Message = string.Format(Strings.Shortcut_Conflict, ShortcutService.Display(gesture), label);
            CanSwap = true;
            return;
        }
        shortcuts.Set(Info.Action, gesture);
        Stop();
    }

    [RelayCommand]
    private void Swap()
    {
        if (_conflict is { } other && _pending is not null)
            shortcuts.Swap(Info.Action, other);
        Stop();
    }

    [RelayCommand]
    private void Cancel() => Stop();

    [RelayCommand]
    private void Reset() => shortcuts.Reset(Info.Action);

    public void Stop()
    {
        IsRecording = false;
        Clear();
    }

    private void Clear()
    {
        _conflict = null;
        _pending = null;
        Message = null;
        CanSwap = false;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Keys));
        OnPropertyChanged(nameof(IsCustom));
    }
}

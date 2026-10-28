using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core;
using Confluir.Core.Rules;
using Confluir.Core.Services;
using Confluir.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public enum SettingsSection
{
    Accounts,
    Rules,
    Fonts,
    Reading,
    Compose,
    Notifications,
    Shortcuts,
    Advanced,
}

/// <summary>Serviços da tela de configurações.</summary>
public sealed record SettingsServices(
    FontService Fonts,
    ISettingsStore Settings,
    AppPaths Paths,
    IMailCache Cache,
    NavigationService Navigation,
    RuleService Rules,
    RuleEditorServices RuleEditor,
    ShortcutService Shortcuts,
    Services.Desktop.DesktopIntegration Desktop,
    Services.Desktop.AutostartService Autostart,
    ILogger<SettingsViewModel> Logger);

public sealed partial class TrustedSenderViewModel(string address, SettingsViewModel owner) : ViewModelBase
{
    public string Address { get; } = address;

    [RelayCommand]
    private void Remove() => owner.RemoveTrustedSender(this);
}

public sealed record MarkReadOption(int Seconds, string LabelKey);

public sealed class AccountNotifyOption(Core.Models.MailAccount account, SettingsViewModel owner)
{
    public Core.Models.MailAccount Account { get; } = account;

    public bool Enabled
    {
        get => owner.IsAccountNotified(Account.Id);
        set => owner.SetAccountNotified(Account.Id, value);
    }
}

public sealed partial class RuleItemViewModel : ViewModelBase
{
    private readonly SettingsViewModel _owner;
    private bool _loading;

    public RuleItemViewModel(MailRule rule, SettingsViewModel owner, bool isFirst = false, bool isLast = false)
    {
        Rule = rule;
        CanMoveUp = !isFirst;
        CanMoveDown = !isLast;
        _owner = owner;
        _loading = true;
        IsEnabled = rule.Enabled;
        _loading = false;
    }

    public MailRule Rule { get; }

    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }

    [RelayCommand]
    private void MoveUp() => _owner.MoveRule(this, -1);

    [RelayCommand]
    private void MoveDown() => _owner.MoveRule(this, +1);

    public string Name => Rule.Name;

    // Pastas aparecem pelo último trecho do nome ("[Gmail]/Promoções" → "Promoções").
    public string Description => RuleText.Describe(Rule, folder => folder.Split('/', '.').Last());

    public string Scope => _owner.AccountLabel(Rule.AccountId);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
            _owner.SetRuleEnabled(this, value);
    }

    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; private set; }

    [ObservableProperty]
    public partial bool IsApplying { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplyResult))]
    public partial string? ApplyResult { get; private set; }

    public bool HasApplyResult => ApplyResult is not null;

    [RelayCommand]
    private void Edit() => _owner.EditRule(this);

    [RelayCommand]
    private void AskDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete() => _owner.DeleteRule(this);

    [RelayCommand]
    private async Task ApplyNowAsync()
    {
        IsApplying = true;
        ApplyResult = null;
        try
        {
            var count = await _owner.ApplyRuleAsync(Rule);
            ApplyResult = string.Format(Strings.Rule_AppliedCount, count);
        }
        catch (Exception)
        {
            ApplyResult = Strings.Rule_ApplyFailed;
        }
        finally
        {
            IsApplying = false;
        }
    }
}

/// <summary>
/// Configurações, em tela própria: contas, fontes, leitura, escrita e avançado (a aparência tem botão próprio).
/// Tudo vale na hora (exceto fontes, que têm pré-visualização e botão de aplicar).
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, INavigationPage
{
    private readonly SettingsServices _services;
    private readonly MainWindowViewModel _main;

    public SettingsViewModel(SettingsServices services, MainWindowViewModel main, SettingsSection section = SettingsSection.Accounts)
    {
        _services = services;
        _main = main;
        Fonts = new FontSettingsViewModel(services.Fonts, services.Logger);
        Section = section;
        MarkReadOptions =
        [
            new(0, nameof(Strings.Settings_MarkRead_Immediately)),
            new(3, nameof(Strings.Settings_MarkRead_After3)),
            new(-1, nameof(Strings.Settings_MarkRead_Never)),
        ];
        var delay = services.Settings.Current.Reading.MarkReadDelaySeconds;
        SelectedMarkRead = MarkReadOptions.FirstOrDefault(o => o.Seconds == delay) ?? MarkReadOptions[0];
        foreach (var sender in services.Settings.Current.Reading.TrustedImageSenders.Order())
            TrustedSenders.Add(new TrustedSenderViewModel(sender, this));
        ReloadRules();
        services.Rules.Changed += (_, _) => ReloadRules();
        Shortcuts = ShortcutInfo.All.Select(s => new ShortcutItemViewModel(s, services.Shortcuts, this)).ToList();
        services.Shortcuts.Changed += (_, _) => RefreshShortcuts();
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.AccountNodes) or "")
                OnPropertyChanged(nameof(HasAccounts));
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccounts), nameof(IsRules), nameof(IsFonts), nameof(IsReading),
        nameof(IsCompose), nameof(IsNotifications), nameof(IsShortcuts), nameof(IsAdvanced))]
    public partial SettingsSection Section { get; set; }

    public bool IsAccounts { get => Section == SettingsSection.Accounts; set { if (value) Section = SettingsSection.Accounts; } }
    public bool IsRules { get => Section == SettingsSection.Rules; set { if (value) Section = SettingsSection.Rules; } }
    public bool IsFonts { get => Section == SettingsSection.Fonts; set { if (value) Section = SettingsSection.Fonts; } }
    public bool IsReading { get => Section == SettingsSection.Reading; set { if (value) Section = SettingsSection.Reading; } }
    public bool IsCompose { get => Section == SettingsSection.Compose; set { if (value) Section = SettingsSection.Compose; } }
    public bool IsNotifications { get => Section == SettingsSection.Notifications; set { if (value) Section = SettingsSection.Notifications; } }
    public bool IsShortcuts { get => Section == SettingsSection.Shortcuts; set { if (value) Section = SettingsSection.Shortcuts; } }
    public bool IsAdvanced { get => Section == SettingsSection.Advanced; set { if (value) Section = SettingsSection.Advanced; } }

    /// <summary>Página na frente (INavigationPage).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [RelayCommand]
    private void Back() => _services.Navigation.GoBack();

    // Contas ----------------------------------------------------------------------------------------

    /// <summary>As mesmas contas da barra lateral (editar e remover funcionam igual).</summary>
    public IEnumerable<AccountNode> Accounts => _main.AccountNodes;

    public bool HasAccounts => _main.AccountNodes.Any();

    [RelayCommand]
    private void AddAccount() => _main.AddAccountCommand.Execute(null);

// Regras ----------------------------------------------------------------------------------------

    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];

    public bool HasRules => Rules.Count > 0;

    private void ReloadRules()
    {
        Rules.Clear();
        var all = _services.Rules.Rules;
        for (var i = 0; i < all.Count; i++)
            Rules.Add(new RuleItemViewModel(all[i], this, isFirst: i == 0, isLast: i == all.Count - 1));
        OnPropertyChanged(nameof(HasRules));
    }

    private IReadOnlyList<Core.Models.MailAccount> AccountList => _main.AccountNodes.Select(n => n.Account).ToList();

    public string AccountLabel(Guid? accountId) =>
        accountId is { } id && AccountList.FirstOrDefault(a => a.Id == id) is { } account
            ? account.DisplayName
            : Strings.Rule_AllAccounts;

    [RelayCommand]
    private void NewRule() =>
        _services.Navigation.NavigateTo(new RuleEditorViewModel(_services.RuleEditor, AccountList, _main.ShowStatus));

    public void EditRule(RuleItemViewModel item) =>
        _services.Navigation.NavigateTo(new RuleEditorViewModel(_services.RuleEditor, AccountList, _main.ShowStatus, item.Rule));

    public void DeleteRule(RuleItemViewModel item) => _services.Rules.Delete(item.Rule.Id);

    public void MoveRule(RuleItemViewModel item, int delta) => _services.Rules.Move(item.Rule.Id, delta);

    public void SetRuleEnabled(RuleItemViewModel item, bool enabled) => _services.Rules.SetEnabled(item.Rule.Id, enabled);

    public async Task<int> ApplyRuleAsync(MailRule rule)
    {
        return await _services.Rules.ApplyNowAsync(rule);
    }

    // Fontes ----------------------------------------------------------------------------------------

    public FontSettingsViewModel Fonts { get; }

    // Leitura ---------------------------------------------------------------------------------------

    public IReadOnlyList<MarkReadOption> MarkReadOptions { get; }

    [ObservableProperty]
    public partial MarkReadOption SelectedMarkRead { get; set; }

    partial void OnSelectedMarkReadChanged(MarkReadOption value)
    {
        if (value is null)
            return;
        _services.Settings.Current.Reading.MarkReadDelaySeconds = value.Seconds;
        _services.Settings.Save();
    }

    public ObservableCollection<TrustedSenderViewModel> TrustedSenders { get; } = [];

    public bool HasTrustedSenders => TrustedSenders.Count > 0;

    public void RemoveTrustedSender(TrustedSenderViewModel sender)
    {
        _services.Settings.Current.Reading.TrustedImageSenders.Remove(sender.Address);
        _services.Settings.Save();
        TrustedSenders.Remove(sender);
        OnPropertyChanged(nameof(HasTrustedSenders));
    }

    // Escrita ---------------------------------------------------------------------------------------

    public bool ComposeRichText
    {
        get => !_services.Settings.Current.Compose.PlainText;
        set
        {
            if (value)
                SetPlainText(false);
        }
    }

    public bool ComposePlainText
    {
        get => _services.Settings.Current.Compose.PlainText;
        set
        {
            if (value)
                SetPlainText(true);
        }
    }

    private void SetPlainText(bool plain)
    {
        if (_services.Settings.Current.Compose.PlainText == plain)
            return;
        _services.Settings.Current.Compose.PlainText = plain;
        _services.Settings.Save();
        OnPropertyChanged(nameof(ComposeRichText));
        OnPropertyChanged(nameof(ComposePlainText));
    }

    public bool IsRichTextAvailable => WebViewSupport.IsAvailable;

    // Notificações e sistema ------------------------------------------------------------------------

    private Core.Settings.NotificationSettings NotificationOptions => _services.Settings.Current.Notifications;
    private Core.Settings.DesktopSettings DesktopOptions => _services.Settings.Current.Desktop;

    public bool NotificationsEnabled
    {
        get => NotificationOptions.Enabled;
        set => SaveSetting(() => NotificationOptions.Enabled = value);
    }

    public bool DoNotDisturb
    {
        get => NotificationOptions.DoNotDisturb;
        set => SaveSetting(() => NotificationOptions.DoNotDisturb = value);
    }

    public bool CloseToTray
    {
        get => DesktopOptions.CloseToTray;
        set => SaveSetting(() => DesktopOptions.CloseToTray = value);
    }

    public bool StartWithSystem
    {
        get => DesktopOptions.StartWithSystem;
        set => SaveSetting(() =>
        {
            DesktopOptions.StartWithSystem = value;
            _services.Autostart.Apply(value);
        });
    }

    public bool HasTray => _services.Desktop.HasTray;

    /// <summary>Uma chave por conta (ligada = notifica).</summary>
    public IReadOnlyList<AccountNotifyOption> AccountNotifications =>
        _main.AccountNodes.Select(n => new AccountNotifyOption(n.Account, this)).ToList();

    public bool IsAccountNotified(Guid accountId) => !NotificationOptions.MutedAccounts.Contains(accountId);

    public void SetAccountNotified(Guid accountId, bool notify) => SaveSetting(() =>
    {
        NotificationOptions.MutedAccounts.Remove(accountId);
        if (!notify)
            NotificationOptions.MutedAccounts.Add(accountId);
    });

    [RelayCommand]
    private void TestNotification() => _services.Desktop.TestNotification();

    private void SaveSetting(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        change();
        _services.Settings.Save();
        OnPropertyChanged(property);
    }

    // Atalhos ---------------------------------------------------------------------------------------

    public IReadOnlyList<ShortcutItemViewModel> Shortcuts { get; }

    public bool HasCustomShortcuts => ShortcutInfo.All.Any(s => _services.Shortcuts.IsCustom(s.Action));

    /// <summary>Linha gravando a nova combinação (só uma por vez).</summary>
    public ShortcutItemViewModel? RecordingShortcut => Shortcuts.FirstOrDefault(s => s.IsRecording);

    public void StartRecording(ShortcutItemViewModel item)
    {
        foreach (var other in Shortcuts.Where(s => s != item))
            other.Stop();
    }

    [RelayCommand]
    private void ResetAllShortcuts() => _services.Shortcuts.ResetAll();

    private void RefreshShortcuts()
    {
        foreach (var item in Shortcuts)
            item.Refresh();
        OnPropertyChanged(nameof(HasCustomShortcuts));
    }

    // Avançado --------------------------------------------------------------------------------------

    public string DataDirectory => _services.Paths.DataDirectory;

    public string LogsDirectory => _services.Paths.LogsDirectory;

    public string Version =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";

    [ObservableProperty]
    public partial bool IsConfirmingClearCache { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClearCacheMessage))]
    public partial string? ClearCacheMessage { get; private set; }

    public bool HasClearCacheMessage => ClearCacheMessage is not null;

    [RelayCommand]
    private void AskClearCache()
    {
        ClearCacheMessage = null;
        IsConfirmingClearCache = true;
    }

    [RelayCommand]
    private void CancelClearCache() => IsConfirmingClearCache = false;

    [RelayCommand]
    private async Task ConfirmClearCacheAsync()
    {
        IsConfirmingClearCache = false;
        try
        {
            await _services.Cache.ClearBodiesAsync();
            ClearCacheMessage = Strings.Settings_ClearCache_Done;
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning(ex, "Falha ao limpar o cache de mensagens");
            ClearCacheMessage = Strings.Settings_ClearCache_Failed;
        }
    }
}

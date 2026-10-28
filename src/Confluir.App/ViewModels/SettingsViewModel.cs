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
    ILogger<SettingsViewModel> Logger);

public sealed partial class TrustedSenderViewModel(string address, SettingsViewModel owner) : ViewModelBase
{
    public string Address { get; } = address;

    [RelayCommand]
    private void Remove() => owner.RemoveTrustedSender(this);
}

public sealed record MarkReadOption(int Seconds, string LabelKey);

public sealed partial class RuleItemViewModel : ViewModelBase
{
    private readonly SettingsViewModel _owner;
    private bool _loading;

    public RuleItemViewModel(MailRule rule, SettingsViewModel owner)
    {
        Rule = rule;
        _owner = owner;
        _loading = true;
        IsEnabled = rule.Enabled;
        _loading = false;
    }

    public MailRule Rule { get; }

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
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.AccountNodes) or "")
                OnPropertyChanged(nameof(HasAccounts));
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccounts), nameof(IsRules), nameof(IsFonts), nameof(IsReading),
        nameof(IsCompose), nameof(IsShortcuts), nameof(IsAdvanced))]
    public partial SettingsSection Section { get; set; }

    public bool IsAccounts { get => Section == SettingsSection.Accounts; set { if (value) Section = SettingsSection.Accounts; } }
    public bool IsRules { get => Section == SettingsSection.Rules; set { if (value) Section = SettingsSection.Rules; } }
    public bool IsFonts { get => Section == SettingsSection.Fonts; set { if (value) Section = SettingsSection.Fonts; } }
    public bool IsReading { get => Section == SettingsSection.Reading; set { if (value) Section = SettingsSection.Reading; } }
    public bool IsCompose { get => Section == SettingsSection.Compose; set { if (value) Section = SettingsSection.Compose; } }
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
        foreach (var rule in _services.Rules.Rules)
            Rules.Add(new RuleItemViewModel(rule, this));
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

    // Atalhos ---------------------------------------------------------------------------------------

    public IReadOnlyList<ShortcutInfo> Shortcuts => ShortcutInfo.All;

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

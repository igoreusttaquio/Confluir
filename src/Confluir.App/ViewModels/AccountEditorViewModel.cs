using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public sealed record SecurityOption(SecurityMode Mode, string Label);

public sealed record AccountColorOption(string Hex, string Name);

public enum TestState
{
    Idle,
    Testing,
    Ok,
    Failed,
}

public sealed partial class TestStatusViewModel(string protocol) : ViewModelBase
{
    public string Protocol { get; } = protocol;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsTesting), nameof(IsOk), nameof(IsFailed))]
    public partial TestState State { get; private set; }

    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    public bool IsVisible => State != TestState.Idle;
    public bool IsTesting => State == TestState.Testing;
    public bool IsOk => State == TestState.Ok;
    public bool IsFailed => State == TestState.Failed;

    public void Set(TestState state, string message = "")
    {
        State = state;
        Message = message;
    }
}

/// <summary>
/// Tela "Adicionar conta": detecta o provedor pelo endereço, preenche os servidores,
/// testa IMAP e SMTP e só então salva (senha no cofre do sistema).
/// </summary>
public sealed partial class AccountEditorViewModel : ViewModelBase
{
    private const string GmailAppPasswordUrl = "https://myaccount.google.com/apppasswords";
    private const string YahooAppPasswordUrl = "https://login.yahoo.com/account/security";

    private readonly IServerSettingsResolver _resolver;
    private readonly IConnectionTester _tester;
    private readonly IAccountStore _accounts;
    private readonly ICredentialStore _credentials;
    private readonly IUrlLauncher _urls;
    private readonly NavigationService _navigation;
    private readonly AccountService _accountService;
    private readonly ILogger<AccountEditorViewModel> _logger;

    private CancellationTokenSource? _detectCts;
    private string? _detectedDomain;
    private string _previousEmail = "";
    private bool _displayNameEdited;
    private bool _applyingPreset;
    private bool _suppressInvalidate;
    private bool _testPassed;

    public AccountEditorViewModel(
        IServerSettingsResolver resolver,
        IConnectionTester tester,
        IAccountStore accounts,
        ICredentialStore credentials,
        IUrlLauncher urls,
        NavigationService navigation,
        AccountService accountService,
        ILogger<AccountEditorViewModel> logger)
    {
        _accountService = accountService;
        _resolver = resolver;
        _tester = tester;
        _accounts = accounts;
        _credentials = credentials;
        _urls = urls;
        _navigation = navigation;
        _logger = logger;

        ColorOptions = AccountColors.All.Select(c => new AccountColorOption(c.Hex, ColorNames.For(c.Key))).ToList();
        // Sugere a primeira cor ainda não usada por outra conta.
        var used = accounts.Accounts.Select(a => a.ColorHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectedColor = ColorOptions.FirstOrDefault(c => !used.Contains(c.Hex)) ?? ColorOptions[0];

        SecurityOptions =
        [
            new SecurityOption(SecurityMode.SslOnConnect, Strings.Security_Ssl),
            new SecurityOption(SecurityMode.StartTls, Strings.Security_StartTls),
            new SecurityOption(SecurityMode.None, Strings.Security_None),
        ];
        ImapSecurity = SecurityOptions[0];
        SmtpSecurity = SecurityOptions[0];
    }

    /// <summary>Conta sendo editada; nulo ao adicionar uma nova.</summary>
    private MailAccount? _editing;

    public bool IsEditing => _editing is not null;
    public string Title => IsEditing ? Strings.EditAccount_Title : Strings.AddAccount_Title;
    public string Subtitle => IsEditing ? Strings.EditAccount_Subtitle : Strings.AddAccount_Subtitle;
    public string PasswordPlaceholder => IsEditing ? Strings.EditAccount_PasswordKeep : "";

    [ObservableProperty]
    public partial bool IsConfirmingRemoval { get; set; }

    /// <summary>Preenche o editor com uma conta existente. A senha fica em branco (mantida se não for alterada).</summary>
    public void Load(MailAccount account)
    {
        _editing = account;
        _applyingPreset = true;
        try
        {
            // Marca o domínio como já detectado: a conta tem servidores próprios que não devem ser sobrescritos.
            _detectedDomain = KnownProviders.GetDomain(account.Email);
            Email = account.Email;
            DisplayName = account.DisplayName;
            _displayNameEdited = true;
            Provider = account.Provider;
            Auth = account.Auth;
            UserName = account.UserName;
            ImapHost = account.Servers.Imap.Host;
            ImapPort = account.Servers.Imap.Port;
            ImapSecurity = SecurityOptions.First(o => o.Mode == account.Servers.Imap.Security);
            SmtpHost = account.Servers.Smtp.Host;
            SmtpPort = account.Servers.Smtp.Port;
            SmtpSecurity = SecurityOptions.First(o => o.Mode == account.Servers.Smtp.Security);
            SelectedColor = ColorOptions.FirstOrDefault(c => c.Hex.Equals(account.ColorHex, StringComparison.OrdinalIgnoreCase))
                ?? SelectedColor;
        }
        finally
        {
            _applyingPreset = false;
        }

        _detectCts?.Cancel();
        DetectionMessage = null;
        // A configuração salva já funcionava: só exige novo teste se a conexão for alterada.
        _testPassed = true;
        ImapStatus.Set(TestState.Idle);
        SmtpStatus.Set(TestState.Idle);
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(PasswordPlaceholder));
    }

    [RelayCommand]
    private void AskRemove() => IsConfirmingRemoval = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemoval = false;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        if (_editing is null)
            return;
        await _accountService.RemoveAsync(_editing);
        _navigation.GoBack();
    }

    public IReadOnlyList<AccountColorOption> ColorOptions { get; }
    public IReadOnlyList<SecurityOption> SecurityOptions { get; }

    public TestStatusViewModel ImapStatus { get; } = new("IMAP");
    public TestStatusViewModel SmtpStatus { get; } = new("SMTP");

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial AccountColorOption SelectedColor { get; set; }

    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string ImapHost { get; set; } = "";

    [ObservableProperty]
    public partial decimal? ImapPort { get; set; } = 993;

    [ObservableProperty]
    public partial SecurityOption ImapSecurity { get; set; }

    [ObservableProperty]
    public partial string SmtpHost { get; set; } = "";

    [ObservableProperty]
    public partial decimal? SmtpPort { get; set; } = 465;

    [ObservableProperty]
    public partial SecurityOption SmtpSecurity { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppPasswordHint), nameof(HasAppPasswordHint), nameof(PasswordLabel))]
    public partial MailProvider Provider { get; private set; } = MailProvider.Imap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresOAuth), nameof(CanSubmit))]
    public partial AuthMethod Auth { get; private set; } = AuthMethod.Password;

    [ObservableProperty]
    public partial bool IsDetecting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectionMessage))]
    public partial string? DetectionMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial bool IsBusy { get; private set; }

    public bool HasDetectionMessage => DetectionMessage is not null;
    public bool HasError => ErrorMessage is not null;
    public bool RequiresOAuth => Auth == AuthMethod.OAuth2;
    public bool CanSubmit => !IsBusy && !RequiresOAuth;

    public string PasswordLabel => Provider is MailProvider.Gmail or MailProvider.Yahoo
        ? Strings.AddAccount_AppPassword
        : Strings.AddAccount_Password;

    public string? AppPasswordHint => Provider switch
    {
        MailProvider.Gmail => Strings.AddAccount_AppPasswordHint_Gmail,
        MailProvider.Yahoo => Strings.AddAccount_AppPasswordHint_Yahoo,
        _ => null,
    };

    public bool HasAppPasswordHint => AppPasswordHint is not null;

    partial void OnEmailChanged(string value)
    {
        // Usuário acompanha o email enquanto não for editado à parte.
        if (UserName.Length == 0 || UserName == _previousEmail.Trim())
            UserName = value.Trim();
        _previousEmail = value;

        _detectCts?.Cancel();
        _detectCts = new CancellationTokenSource();
        _ = DetectAsync(value, _detectCts.Token);
        InvalidateTest();
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (!_applyingPreset)
            _displayNameEdited = value.Length > 0;
    }

    partial void OnPasswordChanged(string value) => InvalidateTest();
    partial void OnUserNameChanged(string value) => InvalidateTest();
    partial void OnImapHostChanged(string value) => OnServerChanged();
    partial void OnImapPortChanged(decimal? value) => OnServerChanged();
    partial void OnImapSecurityChanged(SecurityOption value) => InvalidateTest();
    partial void OnSmtpHostChanged(string value) => OnServerChanged();
    partial void OnSmtpPortChanged(decimal? value) => OnServerChanged();
    partial void OnSmtpSecurityChanged(SecurityOption value) => InvalidateTest();

    /// <summary>Resumo exibido com os servidores recolhidos, ex.: "imap.gmail.com:993 · smtp.gmail.com:465".</summary>
    public string ServerSummary => string.IsNullOrWhiteSpace(ImapHost)
        ? ""
        : $"IMAP {ImapHost.Trim()}:{ImapPort:0}  ·  SMTP {SmtpHost.Trim()}:{SmtpPort:0}";

    public bool HasServerSummary => ServerSummary.Length > 0;

    private void OnServerChanged()
    {
        OnPropertyChanged(nameof(ServerSummary));
        OnPropertyChanged(nameof(HasServerSummary));
        InvalidateTest();
    }

    [RelayCommand]
    private Task OpenAppPasswordHelpAsync() => Provider switch
    {
        MailProvider.Gmail => _urls.OpenAsync(GmailAppPasswordUrl),
        MailProvider.Yahoo => _urls.OpenAsync(YahooAppPasswordUrl),
        _ => Task.CompletedTask,
    };

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await RunTestAsync(ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            if (!_testPassed && !await RunTestAsync(ct))
                return;

            var domain = KnownProviders.GetDomain(Email) ?? Email;
            var account = new MailAccount(
                _editing?.Id ?? Guid.NewGuid(),
                string.IsNullOrWhiteSpace(DisplayName) ? DefaultDisplayName(domain) : DisplayName.Trim(),
                Email.Trim(),
                Provider,
                SelectedColor.Hex,
                UserName.Trim(),
                BuildServers(),
                Auth);

            // Na edição, senha em branco mantém a atual.
            if (!IsEditing || Password.Length > 0)
            {
                try
                {
                    await _credentials.SetAsync(CredentialKeys.Password(account.Id), Password, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Falha ao gravar a credencial no cofre do sistema");
                    ErrorMessage = Strings.AddAccount_CredentialStoreError;
                    return;
                }
            }

            // Mesmo Id substitui a conta existente.
            _accounts.Add(account);
            _logger.LogInformation("Conta {AccountId} {Action} ({Provider})",
                account.Id, IsEditing ? "atualizada" : "adicionada", account.Provider);
            _navigation.GoBack();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _detectCts?.Cancel();
        if (TestCancelCommand.CanExecute(null))
            TestCancelCommand.Execute(null);
        if (SaveCancelCommand.CanExecute(null))
            SaveCancelCommand.Execute(null);
        _navigation.GoBack();
    }

    private async Task<bool> RunTestAsync(CancellationToken ct)
    {
        ErrorMessage = Validate();
        if (ErrorMessage is not null)
            return false;

        ImapStatus.Set(TestState.Testing, Strings.Test_Testing);
        SmtpStatus.Set(TestState.Testing, Strings.Test_Testing);
        try
        {
            // Na edição com senha em branco, testa com a senha guardada no cofre.
            var password = Password.Length == 0 && _editing is not null
                ? await _credentials.GetAsync(CredentialKeys.Password(_editing.Id), ct) ?? ""
                : Password;
            var result = await _tester.TestAsync(Provider, BuildServers(), UserName.Trim(), password, ct);
            ImapStatus.Set(result.Imap.Success ? TestState.Ok : TestState.Failed, ErrorMessages.For(result.Imap));
            SmtpStatus.Set(result.Smtp.Success ? TestState.Ok : TestState.Failed, ErrorMessages.For(result.Smtp));
            if (result.SmtpAlternative is { } alt)
            {
                // A porta configurada não respondeu, mas a alternativa sim: passa a usá-la.
                _suppressInvalidate = true;
                try
                {
                    SmtpPort = alt.Port;
                    SmtpSecurity = SecurityOptions.First(o => o.Mode == alt.Security);
                }
                finally
                {
                    _suppressInvalidate = false;
                }
                SmtpStatus.Set(TestState.Ok, string.Format(Strings.Test_OkAlternativePort, alt.Port, SmtpSecurity.Label));
            }
            // Falha de servidor: abre a configuração avançada para o usuário conferir.
            if (!result.Success && (IsServerError(result.Imap) || IsServerError(result.Smtp)))
                ShowAdvanced = true;
            _testPassed = result.Success;
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            ImapStatus.Set(TestState.Idle);
            SmtpStatus.Set(TestState.Idle);
            return false;
        }
    }

    private static bool IsServerError(ProtocolTestResult r) =>
        r.Error is ConnectionError.ServerUnreachable or ConnectionError.SecureConnectionFailed or ConnectionError.Timeout;

    private async Task DetectAsync(string email, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct); // espera o usuário parar de digitar
            var domain = KnownProviders.GetDomain(email);
            if (domain is null)
            {
                DetectionMessage = null;
                _detectedDomain = null;
                return;
            }
            if (domain == _detectedDomain)
                return;

            IsDetecting = true;
            DetectionMessage = Strings.AddAccount_Detecting;
            var preset = await _resolver.ResolveAsync(email, ct);
            if (ct.IsCancellationRequested || preset is null)
                return;

            _detectedDomain = domain;
            Apply(preset, domain);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao detectar a configuração do domínio");
            DetectionMessage = null;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsDetecting = false;
        }
    }

    private void Apply(ProviderPreset preset, string domain)
    {
        _applyingPreset = true;
        try
        {
            Provider = preset.Provider;
            Auth = preset.Auth;
            UserName = preset.UserName;
            ImapHost = preset.Servers.Imap.Host;
            ImapPort = preset.Servers.Imap.Port;
            ImapSecurity = SecurityOptions.First(o => o.Mode == preset.Servers.Imap.Security);
            SmtpHost = preset.Servers.Smtp.Host;
            SmtpPort = preset.Servers.Smtp.Port;
            SmtpSecurity = SecurityOptions.First(o => o.Mode == preset.Servers.Smtp.Security);
            if (!_displayNameEdited)
                DisplayName = DefaultDisplayName(domain);
        }
        finally
        {
            _applyingPreset = false;
        }

        DetectionMessage = preset.Source switch
        {
            PresetSource.Known => string.Format(Strings.AddAccount_Detected_Known, DefaultDisplayName(domain)),
            PresetSource.Autoconfig => string.Format(Strings.AddAccount_Detected_Autoconfig, domain),
            _ => string.Format(Strings.AddAccount_Detected_Guess, domain),
        };
        if (preset.Source == PresetSource.Guess)
            ShowAdvanced = true;
    }

    private string DefaultDisplayName(string domain) => Provider switch
    {
        MailProvider.Gmail => "Gmail",
        MailProvider.Yahoo => "Yahoo",
        MailProvider.Outlook => "Outlook",
        _ => domain,
    };

    private string? Validate()
    {
        if (KnownProviders.GetDomain(Email) is null)
            return Strings.AddAccount_Invalid_Email;
        if (string.IsNullOrEmpty(Password) && !IsEditing)
            return Strings.AddAccount_Invalid_Password;
        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost) ||
            !IsValidPort(ImapPort) || !IsValidPort(SmtpPort) || string.IsNullOrWhiteSpace(UserName))
        {
            ShowAdvanced = true;
            return Strings.AddAccount_Invalid_Servers;
        }
        return null;
    }

    private static bool IsValidPort(decimal? port) => port is >= 1 and <= 65535;

    private ServerSettings BuildServers() => new(
        new ServerEndpoint(ImapHost.Trim(), (int)(ImapPort ?? 0), ImapSecurity.Mode),
        new ServerEndpoint(SmtpHost.Trim(), (int)(SmtpPort ?? 0), SmtpSecurity.Mode));

    private void InvalidateTest()
    {
        if (_suppressInvalidate || (_applyingPreset && ImapStatus.State == TestState.Idle))
            return;
        _testPassed = false;
        ErrorMessage = null;
        if (!IsBusy)
        {
            ImapStatus.Set(TestState.Idle);
            SmtpStatus.Set(TestState.Idle);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Localization;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public sealed record SecurityOption(SecurityMode Mode, string LabelKey)
{
    public string Label => LocalizationService.Instance[LabelKey];
}

public sealed record AccountColorOption(string Hex, string NameKey);

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
    private readonly IOAuthTokenProvider _oauth;

    // Sessão OAuth obtida nesta tela (ou a da conta em edição) e o token para o teste de conexão.
    private string? _oauthAccountId;
    private string? _oauthToken;
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
        IOAuthTokenProvider oauth,
        ILogger<AccountEditorViewModel> logger)
    {
        _oauth = oauth;
        _accountService = accountService;
        _resolver = resolver;
        _tester = tester;
        _accounts = accounts;
        _credentials = credentials;
        _urls = urls;
        _navigation = navigation;
        _logger = logger;

        ColorOptions = AccountColors.All.Select(c => new AccountColorOption(c.Hex, ColorNames.KeyFor(c.Key))).ToList();
        // Sugere a primeira cor ainda não usada por outra conta.
        var used = accounts.Accounts.Select(a => a.ColorHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectedColor = ColorOptions.FirstOrDefault(c => !used.Contains(c.Hex)) ?? ColorOptions[0];

        SecurityOptions =
        [
            new SecurityOption(SecurityMode.SslOnConnect, nameof(Strings.Security_Ssl)),
            new SecurityOption(SecurityMode.StartTls, nameof(Strings.Security_StartTls)),
            new SecurityOption(SecurityMode.None, nameof(Strings.Security_None)),
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
        _oauthAccountId = account.OAuthAccountId;
        OAuthSignedInAs = account.Auth == AuthMethod.OAuth2 ? account.UserName : null;
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
    [NotifyPropertyChangedFor(nameof(AppPasswordHint), nameof(HasAppPasswordHint), nameof(PasswordLabel), nameof(IsOAuthConfigured),
        nameof(CanSubmit), nameof(ShowOAuthSignIn), nameof(ShowOAuthNotConfigured), nameof(CanChooseAuth), nameof(OAuthSignInText),
        nameof(OAuthHint), nameof(OAuthNotConfiguredText))]
    public partial MailProvider Provider { get; private set; } = MailProvider.Imap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresOAuth), nameof(CanSubmit), nameof(ShowOAuthSignIn), nameof(ShowOAuthNotConfigured),
        nameof(UseOAuth), nameof(UsePassword), nameof(HasAppPasswordHint))]
    public partial AuthMethod Auth { get; private set; } = AuthMethod.Password;

    /// <summary>
    /// Google (Gmail e Workspace): dá para entrar com o Google ou com senha de app. Contas gerenciadas pela empresa
    /// costumam ter a senha de app bloqueada; aí só o login do Google funciona.
    /// </summary>
    public bool CanChooseAuth => Provider == MailProvider.Gmail && _oauth.IsConfigured(MailProvider.Gmail);

    public bool UseOAuth
    {
        get => Auth == AuthMethod.OAuth2;
        set
        {
            if (value)
                ChooseAuth(AuthMethod.OAuth2);
        }
    }

    public bool UsePassword
    {
        get => Auth == AuthMethod.Password;
        set
        {
            if (value)
                ChooseAuth(AuthMethod.Password);
        }
    }

    private void ChooseAuth(AuthMethod auth)
    {
        if (Auth == auth || !CanChooseAuth)
            return;
        Auth = auth;
        ErrorMessage = null;
        InvalidateTest();
    }

    /// <summary>Contas Google entram com o Google quando o app tem o cliente OAuth; senão, com senha de app.</summary>
    private AuthMethod DefaultAuthFor(ProviderPreset preset) =>
        preset.Provider == MailProvider.Gmail
            ? _oauth.IsConfigured(MailProvider.Gmail) ? AuthMethod.OAuth2 : AuthMethod.Password
            : preset.Auth;

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
    public bool CanSubmit => !IsBusy && (!RequiresOAuth || IsOAuthConfigured);

    /// <summary>O app tem as credenciais de aplicativo do login deste provedor (Microsoft ou Google).</summary>
    public bool IsOAuthConfigured => _oauth.IsConfigured(Provider);

    private bool IsGoogle => Provider == MailProvider.Gmail;

    public string OAuthSignInText => IsGoogle ? Strings.OAuth_SignIn_Google : Strings.OAuth_SignIn;
    public string OAuthHint => IsGoogle ? Strings.OAuth_SignInHint_Google : Strings.OAuth_SignInHint;
    public string OAuthNotConfiguredText => IsGoogle ? Strings.OAuth_NotConfigured_Google : Strings.OAuth_NotConfigured;

    public bool ShowOAuthSignIn => RequiresOAuth && IsOAuthConfigured;
    public bool ShowOAuthNotConfigured => RequiresOAuth && !IsOAuthConfigured;

    /// <summary>E-mail com que o usuário entrou na Microsoft; nulo se ainda não entrou.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOAuthSignedIn), nameof(OAuthSignedInText))]
    public partial string? OAuthSignedInAs { get; private set; }

    public bool IsOAuthSignedIn => OAuthSignedInAs is not null;
    public string OAuthSignedInText => OAuthSignedInAs is null ? "" : string.Format(Strings.OAuth_SignedInAs, OAuthSignedInAs);

    [RelayCommand]
    private async Task SignInWithProviderAsync(CancellationToken ct)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _oauth.SignInAsync(Provider, string.IsNullOrWhiteSpace(Email) ? null : Email, ct);
            _oauthAccountId = result.AccountId;
            _oauthToken = result.AccessToken;
            if (string.IsNullOrWhiteSpace(Email))
                Email = result.Email;
            _suppressInvalidate = true;
            try
            {
                UserName = result.Email;
            }
            finally
            {
                _suppressInvalidate = false;
            }
            OAuthSignedInAs = result.Email;
            // Já testa IMAP e SMTP com o token, para o usuário poder salvar em seguida.
            await RunTestAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Login OAuth falhou ({Provider})", Provider);
            ErrorMessage = string.Format(IsGoogle ? Strings.OAuth_SignInFailed_Google : Strings.OAuth_SignInFailed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string PasswordLabel => Provider is MailProvider.Gmail or MailProvider.Yahoo
        ? Strings.AddAccount_AppPassword
        : Strings.AddAccount_Password;

    public string? AppPasswordHint => Provider switch
    {
        MailProvider.Gmail => Strings.AddAccount_AppPasswordHint_Gmail,
        MailProvider.Yahoo => Strings.AddAccount_AppPasswordHint_Yahoo,
        _ => null,
    };

    public bool HasAppPasswordHint => AppPasswordHint is not null && !RequiresOAuth;

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
                Auth,
                Auth == AuthMethod.OAuth2 ? _oauthAccountId ?? _editing?.OAuthAccountId : null)
            {
                // O que esta tela não edita continua como estava (nome do remetente, assinatura).
                SenderName = _editing?.SenderName,
                Signature = _editing?.Signature,
                SignatureOnReplies = _editing?.SignatureOnReplies ?? true,
            };

            // Na edição, senha em branco mantém a atual.
            if (Auth == AuthMethod.Password && (!IsEditing || Password.Length > 0))
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
            var secret = Auth == AuthMethod.OAuth2
                ? _oauthToken ?? (_editing is { OAuthAccountId: not null } ? await _oauth.GetAccessTokenAsync(_editing, ct) : "")
                // Na edição com senha em branco, testa com a senha guardada no cofre.
                : Password.Length == 0 && _editing is not null
                    ? await _credentials.GetAsync(CredentialKeys.Password(_editing.Id), ct) ?? ""
                    : Password;
            var result = await _tester.TestAsync(Provider, BuildServers(), UserName.Trim(), secret, Auth, ct);
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
        catch (ReauthenticationRequiredException)
        {
            ImapStatus.Set(TestState.Idle);
            SmtpStatus.Set(TestState.Idle);
            _oauthToken = null;
            OAuthSignedInAs = null;
            ErrorMessage = Strings.Error_ReauthenticationRequired;
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
            Auth = DefaultAuthFor(preset);
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
            PresetSource.Hosted => string.Format(
                preset.Provider == MailProvider.Gmail ? Strings.AddAccount_Detected_Workspace : Strings.AddAccount_Detected_Microsoft365, domain),
            PresetSource.Autoconfig => string.Format(Strings.AddAccount_Detected_Autoconfig, domain),
            _ => string.Format(Strings.AddAccount_Detected_Guess, domain),
        };
        if (preset.Source == PresetSource.Guess)
            ShowAdvanced = true;
    }

    // Domínio próprio no Google Workspace ou no Microsoft 365: o nome é o domínio (ex.: "empresa.com.br").
    private string DefaultDisplayName(string domain) => KnownProviders.Detect(Email) is null ? domain : Provider switch
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
        if (Auth == AuthMethod.OAuth2)
        {
            if (_oauthToken is null && (_oauthAccountId ?? _editing?.OAuthAccountId) is null)
                return Strings.OAuth_SignInRequired;
        }
        else if (string.IsNullOrEmpty(Password) && !IsEditing)
        {
            return Strings.AddAccount_Invalid_Password;
        }
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

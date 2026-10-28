using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Rules;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

/// <summary>Serviços do editor de regras.</summary>
public sealed record RuleEditorServices(
    RuleService Rules,
    IMailboxService Mailbox,
    NavigationService Navigation,
    ILogger<RuleEditorViewModel> Logger);

/// <param name="Account">Nula = todas as contas.</param>
public sealed record RuleAccountOption(MailAccount? Account, string Label);

public sealed record RuleFieldOption(RuleField Field)
{
    public string Label => RuleText.FieldLabel(Field);
}

public sealed record RuleOperatorOption(RuleOperator Operator)
{
    public string Label => RuleText.OperatorLabel(Operator);
}

public sealed record RuleFolderOption(MailFolder Folder)
{
    public string Label => Folder.Name;
}

public sealed partial class RuleConditionViewModel : ViewModelBase
{
    private readonly RuleEditorViewModel _owner;

    public RuleConditionViewModel(RuleEditorViewModel owner, RuleCondition? condition = null)
    {
        _owner = owner;
        SelectedField = Fields.First(f => f.Field == (condition?.Field ?? RuleField.From));
        SelectedOperator = Operators.First(o => o.Operator == (condition?.Operator ?? RuleOperator.Contains));
        Value = condition?.Value ?? "";
    }

    public static IReadOnlyList<RuleFieldOption> Fields { get; } =
        Enum.GetValues<RuleField>().Select(f => new RuleFieldOption(f)).ToList();

    public static IReadOnlyList<RuleOperatorOption> Operators { get; } =
        Enum.GetValues<RuleOperator>().Select(o => new RuleOperatorOption(o)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue))]
    public partial RuleFieldOption SelectedField { get; set; }

    [ObservableProperty]
    public partial RuleOperatorOption SelectedOperator { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>"Tem anexo" não compara texto.</summary>
    public bool NeedsValue => SelectedField.Field != RuleField.HasAttachment;

    public RuleCondition ToModel() => new(SelectedField.Field, SelectedOperator.Operator, Value.Trim());

    [RelayCommand]
    private void Remove() => _owner.RemoveCondition(this);
}

/// <summary>Criar ou editar uma regra, em tela própria.</summary>
public sealed partial class RuleEditorViewModel : ViewModelBase, INavigationPage
{
    private readonly RuleEditorServices _services;
    private readonly Action<string> _notify;
    private readonly Guid _id;
    private readonly bool _isNew;
    private string? _pendingFolder;

    public RuleEditorViewModel(RuleEditorServices services, IReadOnlyList<MailAccount> accounts, Action<string> notify,
        MailRule? existing = null)
    {
        _services = services;
        _notify = notify;
        _isNew = existing is null;
        _id = existing?.Id ?? Guid.NewGuid();

        AccountOptions =
        [
            new RuleAccountOption(null, Strings.Rule_AllAccounts),
            .. accounts.Select(a => new RuleAccountOption(a, $"{a.DisplayName} — {a.Email}")),
        ];
        Name = existing?.Name ?? "";
        SelectedAccount = AccountOptions.FirstOrDefault(o => o.Account?.Id == existing?.AccountId) ?? AccountOptions[0];
        MatchAll = existing?.Match != RuleMatch.Any;
        foreach (var condition in existing?.Conditions ?? [new RuleCondition(RuleField.From, RuleOperator.Contains, "")])
            Conditions.Add(new RuleConditionViewModel(this, condition));

        var actions = existing?.Actions ?? new RuleActions(false, false, RuleDestination.None);
        MarkRead = actions.MarkRead;
        Flag = actions.Flag;
        Destination = actions.Destination;
        _pendingFolder = actions.MoveToFolder;
        ApplyToExisting = _isNew;
        _ = LoadFoldersAsync();
    }

    /// <summary>Regra nova a partir de uma mensagem: "mensagens deste remetente".</summary>
    public static RuleEditorViewModel FromMessage(RuleEditorServices services, IReadOnlyList<MailAccount> accounts,
        Action<string> notify, MessageSummary message)
    {
        var sender = string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : message.FromName;
        var rule = new MailRule(Guid.NewGuid(), string.Format(Strings.Rule_FromSenderName, sender), true, message.AccountId,
            RuleMatch.All, [new RuleCondition(RuleField.From, RuleOperator.Is, message.FromAddress)],
            new RuleActions(false, false, RuleDestination.None));
        // Já oferece aplicar às mensagens desse remetente que estão na caixa de entrada.
        return new RuleEditorViewModel(services, accounts, notify, rule) { IsPrefilled = true, ApplyToExisting = true };
    }

    public string Title => _isNew || IsPrefilled ? Strings.Rule_New : Strings.Rule_Edit;

    public bool IsPrefilled { get; private init; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    public IReadOnlyList<RuleAccountOption> AccountOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    public partial RuleAccountOption SelectedAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchAny))]
    public partial bool MatchAll { get; set; }

    public bool MatchAny
    {
        get => !MatchAll;
        set => MatchAll = !value;
    }

    public ObservableCollection<RuleConditionViewModel> Conditions { get; } = [];

    [ObservableProperty]
    public partial bool MarkRead { get; set; }

    [ObservableProperty]
    public partial bool Flag { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDestinationNone), nameof(IsDestinationArchive), nameof(IsDestinationDelete),
        nameof(IsDestinationMove))]
    public partial RuleDestination Destination { get; set; }

    public bool IsDestinationNone { get => Destination == RuleDestination.None; set { if (value) Destination = RuleDestination.None; } }
    public bool IsDestinationArchive { get => Destination == RuleDestination.Archive; set { if (value) Destination = RuleDestination.Archive; } }
    public bool IsDestinationDelete { get => Destination == RuleDestination.Delete; set { if (value) Destination = RuleDestination.Delete; } }
    public bool IsDestinationMove { get => Destination == RuleDestination.MoveToFolder; set { if (value) Destination = RuleDestination.MoveToFolder; } }

    /// <summary>Mover para pasta precisa de uma conta (as pastas são de cada conta).</summary>
    public bool CanMove => SelectedAccount.Account is not null;

    [ObservableProperty]
    public partial IReadOnlyList<RuleFolderOption> FolderOptions { get; private set; } = [];

    [ObservableProperty]
    public partial RuleFolderOption? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial bool ApplyToExisting { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public void RemoveCondition(RuleConditionViewModel condition)
    {
        if (Conditions.Count > 1)
            Conditions.Remove(condition);
    }

    [RelayCommand]
    private void AddCondition() => Conditions.Add(new RuleConditionViewModel(this));

    partial void OnSelectedAccountChanged(RuleAccountOption value)
    {
        if (value.Account is null && Destination == RuleDestination.MoveToFolder)
            Destination = RuleDestination.None;
        _ = LoadFoldersAsync();
    }

    private async Task LoadFoldersAsync()
    {
        if (SelectedAccount.Account is not { } account)
        {
            FolderOptions = [];
            SelectedFolder = null;
            return;
        }
        try
        {
            var folders = await _services.Mailbox.GetFoldersAsync(account.Id);
            // Caixa de entrada não faz sentido como destino (as regras rodam nela).
            FolderOptions = folders.Where(f => f.Kind != SpecialFolderKind.Inbox).Select(f => new RuleFolderOption(f)).ToList();
            SelectedFolder = FolderOptions.FirstOrDefault(f => f.Folder.FullName == (_pendingFolder ?? SelectedFolder?.Folder.FullName));
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning(ex, "Falha ao listar pastas para a regra");
            FolderOptions = [];
        }
    }

    [RelayCommand]
    private void Cancel() => _services.Navigation.GoBack();

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = null;
        var conditions = Conditions.Select(c => c.ToModel()).ToList();
        if (conditions.Any(c => c.Field != RuleField.HasAttachment && c.Value.Length == 0))
        {
            Error = Strings.Rule_Error_EmptyCondition;
            return;
        }
        var actions = new RuleActions(MarkRead, Flag, Destination,
            Destination == RuleDestination.MoveToFolder ? SelectedFolder?.Folder.FullName : null);
        if (actions.IsEmpty)
        {
            Error = Strings.Rule_Error_NoAction;
            return;
        }
        if (Destination == RuleDestination.MoveToFolder && actions.MoveToFolder is null)
        {
            Error = Strings.Rule_Error_NoFolder;
            return;
        }

        var rule = new MailRule(_id, "", true, SelectedAccount.Account?.Id, MatchAll ? RuleMatch.All : RuleMatch.Any,
            conditions, actions);
        var existing = _services.Rules.Rules.FirstOrDefault(r => r.Id == _id);
        rule = rule with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? RuleText.Condition(conditions[0]) : Name.Trim(),
            Enabled = existing?.Enabled ?? true,
        };
        _services.Rules.Save(rule);

        if (!ApplyToExisting)
        {
            _notify(Strings.Rule_Saved);
            _services.Navigation.GoBack();
            return;
        }
        IsSaving = true;
        try
        {
            var count = await _services.Rules.ApplyNowAsync(rule);
            _notify(string.Format(Strings.Rule_SavedApplied, count));
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning(ex, "Falha ao aplicar a regra às mensagens existentes");
            _notify(Strings.Rule_Saved);
        }
        finally
        {
            IsSaving = false;
        }
        _services.Navigation.GoBack();
    }

    partial void OnSelectedFolderChanged(RuleFolderOption? value)
    {
        if (value is not null)
            _pendingFolder = null;
    }
}

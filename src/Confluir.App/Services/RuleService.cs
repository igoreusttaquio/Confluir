using Confluir.App.Resources;
using Confluir.Core.Rules;
using Confluir.Core.Services;
using Confluir.Core.Settings;

namespace Confluir.App.Services;

/// <summary>
/// Regras guardadas nas configurações. A lista é sempre substituída por uma nova (nunca alterada no lugar),
/// porque a sincronização a lê em segundo plano.
/// </summary>
public sealed class RuleService(ISettingsStore settings, IMailboxService mailbox)
{
    public event EventHandler? Changed;

    /// <summary>Uma regra foi aplicada às mensagens existentes (a lista precisa recarregar).</summary>
    public event EventHandler? Applied;

    public IReadOnlyList<MailRule> Rules => settings.Current.Rules;

    public void Save(MailRule rule)
    {
        var list = Rules.ToList();
        var index = list.FindIndex(r => r.Id == rule.Id);
        if (index >= 0)
            list[index] = rule;
        else
            list.Add(rule);
        Store(list);
    }

    public void Delete(Guid id) => Store(Rules.Where(r => r.Id != id).ToList());

    /// <summary>Sobe (-1) ou desce (+1) a regra: elas rodam na ordem da lista.</summary>
    public void Move(Guid id, int delta)
    {
        var list = Rules.ToList();
        var index = list.FindIndex(r => r.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
            return;
        (list[index], list[target]) = (list[target], list[index]);
        Store(list);
    }

    public void SetEnabled(Guid id, bool enabled) =>
        Store(Rules.Select(r => r.Id == id ? r with { Enabled = enabled } : r).ToList());

    /// <summary>Roda a regra nas mensagens que já estão na caixa de entrada.</summary>
    public async Task<int> ApplyNowAsync(MailRule rule)
    {
        var count = await mailbox.ApplyRuleAsync(rule);
        if (count > 0)
            Applied?.Invoke(this, EventArgs.Empty);
        return count;
    }

    private void Store(List<MailRule> rules)
    {
        settings.Current.Rules = rules;
        settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Descrição de uma regra em linguagem natural ("Quando o remetente contém “x”: arquivar").</summary>
public static class RuleText
{
    public static string FieldLabel(RuleField field) => field switch
    {
        RuleField.From => Strings.Rule_Field_From,
        RuleField.To => Strings.Rule_Field_To,
        RuleField.Subject => Strings.Rule_Field_Subject,
        _ => Strings.Rule_Field_HasAttachment,
    };

    public static string OperatorLabel(RuleOperator op) => op switch
    {
        RuleOperator.Contains => Strings.Rule_Op_Contains,
        RuleOperator.NotContains => Strings.Rule_Op_NotContains,
        RuleOperator.Is => Strings.Rule_Op_Is,
        RuleOperator.StartsWith => Strings.Rule_Op_StartsWith,
        _ => Strings.Rule_Op_EndsWith,
    };

    public static string Condition(RuleCondition c) =>
        c.Field == RuleField.HasAttachment
            ? Strings.Rule_Field_HasAttachment.ToLowerInvariant()
            : $"{FieldLabel(c.Field).ToLowerInvariant()} {OperatorLabel(c.Operator)} “{c.Value.Trim()}”";

    public static string Actions(RuleActions a, Func<string, string>? folderName = null)
    {
        var parts = new List<string>();
        if (a.MarkRead)
            parts.Add(Strings.Rule_Action_MarkRead);
        if (a.Flag)
            parts.Add(Strings.Rule_Action_Flag);
        switch (a.Destination)
        {
            case RuleDestination.Archive:
                parts.Add(Strings.Rule_Action_Archive);
                break;
            case RuleDestination.Delete:
                parts.Add(Strings.Rule_Action_Delete);
                break;
            case RuleDestination.MoveToFolder when a.MoveToFolder is { } folder:
                parts.Add(string.Format(Strings.Rule_Action_MoveTo, folderName?.Invoke(folder) ?? folder));
                break;
        }
        return string.Join(", ", parts).ToLowerInvariant() is { Length: > 0 } text
            ? char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..]
            : "";
    }

    public static string Describe(MailRule rule, Func<string, string>? folderName = null)
    {
        var joiner = rule.Match == RuleMatch.All ? Strings.Rule_And : Strings.Rule_Or;
        var conditions = string.Join($" {joiner} ", rule.Conditions.Select(Condition));
        return string.Format(Strings.Rule_Describe, conditions, Actions(rule.Actions, folderName));
    }
}

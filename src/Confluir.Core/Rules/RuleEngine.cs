using Confluir.Core.Models;

namespace Confluir.Core.Rules;

/// <summary>O que fazer com uma mensagem depois de passar por todas as regras.</summary>
public sealed record RuleOutcome(MessageSummary Message, RuleActions Actions, IReadOnlyList<Guid> RuleIds);

/// <summary>
/// Avalia regras. As regras rodam em ordem; marcar como lida e sinalizar se somam, e a primeira regra com destino
/// (arquivar, excluir, mover) decide para onde a mensagem vai — as seguintes não mudam mais o destino.
/// </summary>
public static class RuleEngine
{
    public static bool AppliesTo(MailRule rule, Guid accountId) =>
        rule.Enabled && (rule.AccountId is null || rule.AccountId == accountId) && rule.Conditions.Count > 0 &&
        !rule.Actions.IsEmpty;

    public static bool Matches(MailRule rule, MessageSummary message)
    {
        if (rule.Conditions.Count == 0)
            return false;
        return rule.Match == RuleMatch.All
            ? rule.Conditions.All(c => Matches(c, message))
            : rule.Conditions.Any(c => Matches(c, message));
    }

    public static bool Matches(RuleCondition condition, MessageSummary message)
    {
        if (condition.Field == RuleField.HasAttachment)
            return message.HasAttachments;

        var value = condition.Value.Trim();
        if (value.Length == 0)
            return false;
        IEnumerable<string> targets = condition.Field switch
        {
            RuleField.From => [message.FromAddress, message.FromName],
            RuleField.To => (message.Recipients ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            _ => [message.Subject],
        };
        var list = targets.Where(t => !string.IsNullOrEmpty(t)).ToList();

        // "Não contém" vale quando nenhum dos campos contém o texto.
        if (condition.Operator == RuleOperator.NotContains)
            return list.All(t => !t.Contains(value, StringComparison.OrdinalIgnoreCase));
        return list.Any(t => condition.Operator switch
        {
            RuleOperator.Contains => t.Contains(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.Is => t.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.StartsWith => t.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.EndsWith => t.EndsWith(value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        });
    }

    /// <summary>Ações combinadas por mensagem (só as que alguma regra pegou e que mudam algo).</summary>
    public static IReadOnlyList<RuleOutcome> Evaluate(IEnumerable<MailRule> rules, Guid accountId,
        IEnumerable<MessageSummary> messages)
    {
        var active = rules.Where(r => AppliesTo(r, accountId)).ToList();
        if (active.Count == 0)
            return [];

        var outcomes = new List<RuleOutcome>();
        foreach (var message in messages)
        {
            var markRead = false;
            var flag = false;
            var destination = RuleDestination.None;
            string? folder = null;
            var matched = new List<Guid>();
            foreach (var rule in active.Where(r => Matches(r, message)))
            {
                matched.Add(rule.Id);
                markRead |= rule.Actions.MarkRead;
                flag |= rule.Actions.Flag;
                if (destination == RuleDestination.None && rule.Actions.Destination != RuleDestination.None &&
                    (rule.Actions.Destination != RuleDestination.MoveToFolder || !string.IsNullOrEmpty(rule.Actions.MoveToFolder)))
                {
                    destination = rule.Actions.Destination;
                    folder = rule.Actions.MoveToFolder;
                }
            }
            // Já lida/sinalizada: não há o que fazer nessa parte.
            var actions = new RuleActions(markRead && !message.IsRead, flag && !message.IsFlagged, destination, folder);
            if (matched.Count > 0 && !actions.IsEmpty)
                outcomes.Add(new RuleOutcome(message, actions, matched));
        }
        return outcomes;
    }
}

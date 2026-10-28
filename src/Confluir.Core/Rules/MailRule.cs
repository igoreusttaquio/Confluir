namespace Confluir.Core.Rules;

public enum RuleField
{
    From,
    /// <summary>Destinatários (Para e Cc).</summary>
    To,
    Subject,
    HasAttachment,
}

public enum RuleOperator
{
    Contains,
    NotContains,
    Is,
    StartsWith,
    EndsWith,
}

/// <summary>Todas as condições precisam bater, ou basta uma.</summary>
public enum RuleMatch
{
    All,
    Any,
}

/// <summary>Para onde a mensagem vai (no máximo um destino por mensagem).</summary>
public enum RuleDestination
{
    None,
    Archive,
    Delete,
    MoveToFolder,
}

/// <param name="Value">Texto comparado sem diferenciar maiúsculas; ignorado em <see cref="RuleField.HasAttachment"/>.</param>
public sealed record RuleCondition(RuleField Field, RuleOperator Operator, string Value);

/// <param name="MoveToFolder">Nome completo da pasta no servidor, quando <see cref="Destination"/> é MoveToFolder.</param>
public sealed record RuleActions(bool MarkRead, bool Flag, RuleDestination Destination, string? MoveToFolder = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => !MarkRead && !Flag && Destination == RuleDestination.None;
}

/// <summary>
/// Regra aplicada às mensagens novas da caixa de entrada.
/// </summary>
/// <param name="AccountId">Conta onde vale; nulo vale para todas (mover para pasta exige uma conta).</param>
public sealed record MailRule(
    Guid Id,
    string Name,
    bool Enabled,
    Guid? AccountId,
    RuleMatch Match,
    IReadOnlyList<RuleCondition> Conditions,
    RuleActions Actions);

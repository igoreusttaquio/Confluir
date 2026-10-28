using Confluir.Core.Models;
using Confluir.Core.Rules;

namespace Confluir.Core.Tests;

public sealed class RuleEngineTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private static MessageSummary Message(string from = "news@loja.com", string name = "Loja", string subject = "Ofertas",
        bool read = false, bool flagged = false, bool attachments = false, string? recipients = "eu@exemplo.com") =>
        new(Guid.NewGuid(), Account, Guid.NewGuid(), name, from, subject, "", DateTimeOffset.UnixEpoch, read, flagged,
            attachments, recipients);

    private static MailRule Rule(RuleActions actions, RuleMatch match = RuleMatch.All, Guid? account = null, bool enabled = true,
        params RuleCondition[] conditions) =>
        new(Guid.NewGuid(), "r", enabled, account, match, conditions, actions);

    private static readonly RuleActions Archive = new(false, false, RuleDestination.Archive);

    [Theory]
    [InlineData(RuleField.From, RuleOperator.Contains, "LOJA.com", true)]       // sem diferenciar maiúsculas
    [InlineData(RuleField.From, RuleOperator.Is, "news@loja.com", true)]
    [InlineData(RuleField.From, RuleOperator.Is, "loja.com", false)]
    [InlineData(RuleField.From, RuleOperator.EndsWith, "@loja.com", true)]
    [InlineData(RuleField.From, RuleOperator.Contains, "Loja", true)]           // nome do remetente também conta
    [InlineData(RuleField.From, RuleOperator.NotContains, "loja", false)]
    [InlineData(RuleField.Subject, RuleOperator.StartsWith, "ofer", true)]
    [InlineData(RuleField.To, RuleOperator.Is, "eu@exemplo.com", true)]
    [InlineData(RuleField.To, RuleOperator.Contains, "outro", false)]
    [InlineData(RuleField.Subject, RuleOperator.Contains, "  ", false)]         // texto vazio nunca bate
    public void Condicoes(RuleField field, RuleOperator op, string value, bool expected) =>
        Assert.Equal(expected, RuleEngine.Matches(new RuleCondition(field, op, value), Message()));

    [Fact]
    public void Tem_anexo_ignora_o_texto()
    {
        var condition = new RuleCondition(RuleField.HasAttachment, RuleOperator.Contains, "");
        Assert.True(RuleEngine.Matches(condition, Message(attachments: true)));
        Assert.False(RuleEngine.Matches(condition, Message(attachments: false)));
    }

    [Fact]
    public void Todas_ou_qualquer_uma()
    {
        RuleCondition[] conditions =
        [
            new(RuleField.From, RuleOperator.Contains, "loja"),
            new(RuleField.Subject, RuleOperator.Contains, "fatura"),
        ];
        Assert.False(RuleEngine.Matches(Rule(Archive, RuleMatch.All, conditions: conditions), Message()));
        Assert.True(RuleEngine.Matches(Rule(Archive, RuleMatch.Any, conditions: conditions), Message()));
    }

    [Fact]
    public void Acoes_se_somam_e_o_primeiro_destino_vence()
    {
        var from = new RuleCondition(RuleField.From, RuleOperator.Contains, "loja");
        var rules = new[]
        {
            Rule(new RuleActions(true, false, RuleDestination.None), conditions: from),
            Rule(new RuleActions(false, false, RuleDestination.MoveToFolder, "Promoções"), conditions: from),
            Rule(new RuleActions(false, true, RuleDestination.Delete), conditions: from),
        };

        var outcome = Assert.Single(RuleEngine.Evaluate(rules, Account, [Message()]));

        Assert.Equal(new RuleActions(true, true, RuleDestination.MoveToFolder, "Promoções"), outcome.Actions);
        Assert.Equal(3, outcome.RuleIds.Count);
    }

    [Fact]
    public void Ignora_regras_desligadas_de_outra_conta_e_o_que_ja_esta_feito()
    {
        var from = new RuleCondition(RuleField.From, RuleOperator.Contains, "loja");
        var markRead = new RuleActions(true, false, RuleDestination.None);

        Assert.Empty(RuleEngine.Evaluate([Rule(Archive, enabled: false, conditions: from)], Account, [Message()]));
        Assert.Empty(RuleEngine.Evaluate([Rule(Archive, account: Guid.NewGuid(), conditions: from)], Account, [Message()]));
        // Já lida: marcar como lida não muda nada, então nem aparece.
        Assert.Empty(RuleEngine.Evaluate([Rule(markRead, conditions: from)], Account, [Message(read: true)]));
        Assert.Single(RuleEngine.Evaluate([Rule(Archive, account: Account, conditions: from)], Account, [Message(), Message(from: "x@y.com", name: "X")]));
    }

    [Fact]
    public void Mover_sem_pasta_nao_conta_como_destino()
    {
        var from = new RuleCondition(RuleField.From, RuleOperator.Contains, "loja");
        var rules = new[]
        {
            Rule(new RuleActions(false, false, RuleDestination.MoveToFolder, null), conditions: from),
            Rule(Archive, conditions: from),
        };

        Assert.Equal(RuleDestination.Archive, Assert.Single(RuleEngine.Evaluate(rules, Account, [Message()])).Actions.Destination);
    }
}

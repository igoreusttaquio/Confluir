using Confluir.App.DesignData;
using Confluir.App.Services;
using Confluir.Core.Rules;
using Confluir.Core.Settings;

namespace Confluir.App.Tests;

public sealed class RuleServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"confluir-rules-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    private static MailRule Rule(string name) => new(Guid.NewGuid(), name, true, null, RuleMatch.All,
        [new RuleCondition(RuleField.From, RuleOperator.Contains, name)], new RuleActions(true, false, RuleDestination.None));

    [Fact]
    public void Subir_e_descer_trocam_a_ordem_e_param_nas_pontas()
    {
        var service = new RuleService(new JsonSettingsStore(_file), new FakeMailboxService(TimeProvider.System));
        var (a, b, c) = (Rule("a"), Rule("b"), Rule("c"));
        service.Save(a);
        service.Save(b);
        service.Save(c);

        service.Move(c.Id, -1);
        Assert.Equal(["a", "c", "b"], service.Rules.Select(r => r.Name));

        service.Move(a.Id, -1); // já é a primeira
        service.Move(b.Id, +1); // já é a última
        Assert.Equal(["a", "c", "b"], service.Rules.Select(r => r.Name));

        // A ordem fica salva nas configurações.
        Assert.Equal(["a", "c", "b"], new JsonSettingsStore(_file).Current.Rules.Select(r => r.Name));
    }

    [Fact]
    public async Task Aplicar_sincroniza_as_contas_da_regra()
    {
        var mailbox = new FakeMailboxService(TimeProvider.System);
        var service = new RuleService(new JsonSettingsStore(_file), mailbox);
        var account = Guid.NewGuid();

        await service.ApplyNowAsync(Rule("todas"));
        await service.ApplyNowAsync(Rule("uma") with { AccountId = account });

        // Nula = todas as contas; com conta, só ela.
        Assert.Equal([null, account], mailbox.SyncRequests);
    }
}

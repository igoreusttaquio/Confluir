using Confluir.Core.Sync;

namespace Confluir.Core.Tests;

public class SyncPlannerTests
{
    [Fact]
    public void Primeira_sincronizacao_baixa_tudo_do_mais_novo_para_o_mais_antigo_em_lotes()
    {
        var server = Enumerable.Range(1, 1200).Select(i => (uint)i).ToList();

        var plan = SyncPlanner.Plan(null, 7, server, [], chunkSize: 500);

        Assert.False(plan.Reset);
        Assert.Equal([500, 500, 200], plan.FetchChunks.Select(c => c.Count));
        Assert.Equal(1200u, plan.FetchChunks[0][0]);   // começa pelo mais novo
        Assert.Equal(1u, plan.FetchChunks[^1][^1]);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Incremental_baixa_so_as_novas_e_remove_as_apagadas()
    {
        uint[] cached = [10, 11, 12, 13];
        uint[] server = [10, 12, 13, 14, 15]; // 11 foi apagada; 14 e 15 chegaram

        var plan = SyncPlanner.Plan(7, 7, server, cached);

        Assert.False(plan.Reset);
        Assert.Equal([15u, 14u], plan.FetchChunks.Single());
        Assert.Equal([11u], plan.Remove);
    }

    [Fact]
    public void Retoma_preenchimento_interrompido()
    {
        // Só os 2 mais novos foram baixados antes de o app fechar.
        uint[] cached = [9, 10];
        uint[] server = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        var plan = SyncPlanner.Plan(7, 7, server, cached, chunkSize: 5);

        Assert.Equal([[8u, 7, 6, 5, 4], [3u, 2, 1]], plan.FetchChunks.Select(c => c.ToArray()));
    }

    [Fact]
    public void UIDVALIDITY_diferente_refaz_a_pasta()
    {
        uint[] cached = [1, 2, 3];
        uint[] server = [1, 2];

        var plan = SyncPlanner.Plan(7, 8, server, cached);

        Assert.True(plan.Reset);
        Assert.Equal([2u, 1u], plan.FetchChunks.Single());
        Assert.Empty(plan.Remove); // o reset já apaga tudo
    }

    [Fact]
    public void Nada_a_fazer_quando_igual()
    {
        uint[] uids = [1, 2, 3];
        var plan = SyncPlanner.Plan(7, 7, uids, uids);

        Assert.False(plan.Reset);
        Assert.Empty(plan.FetchChunks);
        Assert.Empty(plan.Remove);
        Assert.Equal(0, plan.FetchCount);
    }

    [Fact]
    public void Pasta_vazia_no_servidor_remove_tudo_do_cache()
    {
        var plan = SyncPlanner.Plan(7, 7, [], [1, 2]);
        Assert.Equal([1u, 2u], plan.Remove);
    }
}

using Confluir.Core.Search;

namespace Confluir.Core.Tests;

public sealed class SearchQueryTests
{
    [Fact]
    public void Texto_solto_e_operadores()
    {
        var q = SearchQuery.Parse("orçamento de:ana para:\"time vendas\" assunto:proposta tem:anexo não:lida");

        Assert.Equal(["orçamento"], q.Terms);
        Assert.Equal(["ana"], q.From);
        Assert.Equal(["time vendas"], q.To);
        Assert.Equal(["proposta"], q.Subject);
        Assert.True(q.HasAttachment);
        Assert.True(q.Unread);
    }

    [Theory]
    [InlineData("from:ana")]
    [InlineData("DE:ana")]
    [InlineData("remetente:ana")]
    public void Operador_de_remetente_em_varias_linguas(string text) =>
        Assert.Equal(["ana"], SearchQuery.Parse(text).From);

    [Theory]
    [InlineData("has:attachment")]
    [InlineData("tiene:adjunto")]
    [InlineData("tem:anexos")]
    public void Anexo_em_varias_linguas(string text) => Assert.True(SearchQuery.Parse(text).HasAttachment);

    [Fact]
    public void Operador_desconhecido_vira_texto_e_vazio_e_ignorado()
    {
        var q = SearchQuery.Parse("hora:10 de: \"\" -- ");

        Assert.Equal(["hora:10"], q.Terms);
        Assert.Empty(q.From);
        Assert.False(SearchQuery.Parse("  ").HasText);
        Assert.True(SearchQuery.Parse("  ").IsEmpty);
    }

    [Fact]
    public void Expressao_do_fts_escapa_aspas_e_usa_prefixo()
    {
        var q = SearchQuery.Parse("relat de:ana assunto:\"diz \"\"oi\"\"\"");

        Assert.Equal("\"relat\"* AND sender : \"ana\"*", SearchQuery.Parse("relat de:ana").ToFtsMatch());
        Assert.DoesNotContain("\"\"\"\"", q.ToFtsMatch()); // nunca gera aspas soltas
        Assert.Null(SearchQuery.Parse("tem:anexo").ToFtsMatch());
    }
}

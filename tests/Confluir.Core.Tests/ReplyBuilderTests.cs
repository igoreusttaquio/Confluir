using Confluir.Core.Compose;
using Confluir.Core.Models;

namespace Confluir.Core.Tests;

public class ReplyBuilderTests
{
    private static readonly MailAddress Ana = new("Ana", "ana@exemplo.com");
    private static readonly MailAddress Bruno = new("Bruno", "bruno@exemplo.com");
    private static readonly MailAddress Eu = new("Eu", "eu@exemplo.com");
    private static readonly MailAddress Carla = new("", "carla@exemplo.com");

    [Theory]
    [InlineData("Relatório", ComposeMode.Reply, "Re: Relatório")]
    [InlineData("Re: Relatório", ComposeMode.Reply, "Re: Relatório")]
    [InlineData("RES: RE: Relatório", ComposeMode.ReplyAll, "Re: Relatório")]
    [InlineData("Re[2]: Relatório", ComposeMode.Reply, "Re: Relatório")]
    [InlineData("ENC: Relatório", ComposeMode.Forward, "Fwd: Relatório")]
    [InlineData("Re: Relatório", ComposeMode.Forward, "Fwd: Relatório")]
    [InlineData("Resumo do mês", ComposeMode.Reply, "Re: Resumo do mês")] // "Res" no início de palavra não é prefixo
    public void Assunto_sem_acumular_prefixos(string original, ComposeMode mode, string expected)
    {
        Assert.Equal(expected, ReplyBuilder.Subject(original, mode));
    }

    [Fact]
    public void Responder_vai_para_o_remetente_ou_reply_to()
    {
        Assert.Equal([Ana], ReplyBuilder.Recipients(ComposeMode.Reply, Ana, null, [Eu], [], Eu.Address).To);
        var replyTo = new MailAddress("Lista", "lista@exemplo.com");
        Assert.Equal([replyTo], ReplyBuilder.Recipients(ComposeMode.Reply, Ana, [replyTo], [Eu], [], Eu.Address).To);
    }

    [Fact]
    public void Responder_a_todos_inclui_os_demais_sem_mim_e_sem_repeticoes()
    {
        var (to, cc) = ReplyBuilder.Recipients(ComposeMode.ReplyAll, Ana, null,
            to: [Eu, Bruno, Ana], cc: [Carla, new MailAddress("", "BRUNO@exemplo.com")], ownAddress: "EU@exemplo.com");

        Assert.Equal([Ana], to);
        Assert.Equal([Bruno, Carla], cc);
    }

    [Fact]
    public void Responder_a_mensagem_enviada_por_mim_volta_para_mim()
    {
        Assert.Equal([Eu], ReplyBuilder.Recipients(ComposeMode.Reply, Eu, null, [Ana], [], Eu.Address).To);
    }

    [Fact]
    public void Citacao_e_cadeia_de_referencias()
    {
        Assert.Equal("> Olá\n>\n> Tchau", ReplyBuilder.Quote("Olá\r\n\r\nTchau"));
        Assert.Equal(["<a@x>", "<b@x>"], ReplyBuilder.References(["<a@x>"], "<b@x>"));
        Assert.Equal(["<b@x>"], ReplyBuilder.References(null, "<b@x>"));
        Assert.Empty(ReplyBuilder.References(null, null));
    }
}

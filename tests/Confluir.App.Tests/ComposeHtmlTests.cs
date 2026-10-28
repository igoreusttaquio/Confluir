using Confluir.App.Services;
using Confluir.Core.Compose;
using Confluir.Core.Models;

namespace Confluir.App.Tests;

public sealed class ComposeHtmlTests
{
    private static MessageBody Body(string text, string? html = null) =>
        new(Guid.NewGuid(), [new MailAddress("Ana", "ana@exemplo.com")], [], text, [], html);

    [Fact]
    public void Texto_vira_html_escapado_com_linhas_e_links()
    {
        var html = ComposeHtml.FromText("Olá <b>\n\nveja https://confluir.app");

        Assert.Equal("<div>Olá &lt;b&gt;</div><div><br></div><div>veja <a href=\"https://confluir.app\">https://confluir.app</a></div>", html);
    }

    [Fact]
    public void Citacao_remove_scripts_e_folhas_de_estilo_do_original()
    {
        var body = Body("texto", "<html><head><style>body{background:red}</style></head><body><p onclick=\"x()\">Oi</p><script>alert(1)</script></body></html>");

        var reply = ComposeHtml.Reply("Em 1/1, Ana escreveu:", body);

        Assert.Contains("<blockquote", reply);
        Assert.Contains("<p>Oi</p>", reply);
        Assert.DoesNotContain("script", reply);
        Assert.DoesNotContain("onclick", reply);
        Assert.DoesNotContain("background:red", reply);
        Assert.StartsWith("<div><br></div>", reply); // espaço para escrever acima da citação
    }

    [Fact]
    public void Encaminhar_sem_html_usa_o_texto_e_os_cabecalhos()
    {
        var html = ComposeHtml.Forward("---- Encaminhada ----", [("De", "Ana <ana@exemplo.com>")], Body("linha 1\nlinha 2"));

        Assert.Contains("<div>De: Ana &lt;ana@exemplo.com&gt;</div>", html);
        Assert.Contains("<div>linha 1</div><div>linha 2</div>", html);
    }

    [Fact]
    public void Rascunho_para_o_servidor_ignora_enderecos_incompletos()
    {
        var draft = new Draft(Guid.NewGuid(), Guid.NewGuid(), ComposeMode.New, "ana@exemplo.com, bia@", "", "", "Oi",
            "<div><b>Oi</b> Ana</div>", "Oi Ana", IsPlainText: false, [], null, null, DateTimeOffset.UnixEpoch);

        var message = DraftService.ToOutgoing(draft);

        Assert.Equal([new MailAddress("", "ana@exemplo.com")], message.To);
        Assert.Contains("<b>Oi</b> Ana", message.HtmlBody);
        Assert.Equal("Oi Ana", message.TextBody);

        var plain = DraftService.ToOutgoing(draft with { IsPlainText = true, Text = "só texto" });
        Assert.Null(plain.HtmlBody);
        Assert.Equal("só texto", plain.TextBody);
    }
}

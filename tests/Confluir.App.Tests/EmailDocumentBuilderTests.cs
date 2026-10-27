using Confluir.App.Services;
using Confluir.Core.Models;

namespace Confluir.App.Tests;

public class EmailDocumentBuilderTests
{
    private static readonly ThemePalette Dark = new("#0B1020", "#111833", "#1A2347", "#26305A", "#E8ECFF", "#9AA3C7", "#7B97FF", IsDark: true);

    private static MessageBody Body(string text = "", string? html = null) =>
        new(Guid.NewGuid(), [], [], text, [], html);

    [Fact]
    public void Politica_de_seguranca_bloqueia_scripts_e_rede_por_padrao()
    {
        var doc = EmailDocumentBuilder.Build(Body(html: "<p>oi</p>"), allowRemoteImages: false, Dark, "sans-serif");

        Assert.Contains("Content-Security-Policy", doc.Html);
        Assert.Contains("default-src 'none'", doc.Html);
        Assert.Contains("img-src data: cid:;", doc.Html);
        Assert.DoesNotContain("script-src", doc.Html);
        Assert.DoesNotContain("https:", doc.Html.Split("Content-Security-Policy")[1].Split('>')[0]);
    }

    [Fact]
    public void Liberar_imagens_abre_a_politica_e_mostra_o_conteudo()
    {
        var html = "<img src=\"https://cdn.example/a.png\">";

        var blocked = EmailDocumentBuilder.Build(Body(html: html), allowRemoteImages: false, Dark, "sans-serif");
        var allowed = EmailDocumentBuilder.Build(Body(html: html), allowRemoteImages: true, Dark, "sans-serif");

        Assert.Equal(1, blocked.BlockedRemoteImages);
        Assert.DoesNotContain("cdn.example", blocked.Html);
        Assert.Equal(0, allowed.BlockedRemoteImages);
        Assert.Contains("cdn.example", allowed.Html);
        Assert.Contains("img-src data: cid: https: http:", allowed.Html);
    }

    [Fact]
    public void Texto_puro_e_escapado_e_ganha_links()
    {
        var doc = EmailDocumentBuilder.Build(
            Body("Oi <script>x</script>, veja https://exemplo.com/a?b=1&c=2. Fale com ana@exemplo.com"),
            allowRemoteImages: false, Dark, "sans-serif");

        Assert.Contains("&lt;script&gt;", doc.Html);
        Assert.Contains("<a href=\"https://exemplo.com/a?b=1&amp;c=2\">", doc.Html);
        Assert.Contains("</a>. Fale", doc.Html); // ponto final fora do link
        Assert.Contains("<a href=\"mailto:ana@exemplo.com\">", doc.Html);
        Assert.Contains("class=\"plain\"", doc.Html);
    }

    [Fact]
    public void Email_simples_usa_as_cores_do_tema()
    {
        var doc = EmailDocumentBuilder.Build(Body(html: "<p>oi</p>"), false, Dark, "sans-serif");
        Assert.Contains(".mail { background: #111833; color: #E8ECFF;", doc.Html);
        Assert.Contains("content=\"dark\"", doc.Html);
    }

    [Fact]
    public void Email_com_fundo_proprio_fica_em_papel_branco_no_tema_escuro()
    {
        var doc = EmailDocumentBuilder.Build(Body(html: "<table bgcolor=\"#f4f4f4\"><tr><td>oferta</td></tr></table>"), false, Dark, "sans-serif");
        Assert.Contains(".mail { background: #FFFFFF; color: #141A33;", doc.Html);
        Assert.Contains("content=\"light\"", doc.Html);
    }
}

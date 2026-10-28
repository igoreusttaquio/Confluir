using Confluir.App.Services;
using Confluir.Core.Models;

namespace Confluir.App.Tests;

public class EmailDocumentBuilderTests
{
    private static readonly ThemePalette Dark = new("#0B1020", "#111833", "#1A2347", "#26305A", "#E8ECFF", "#9AA3C7", "#7B97FF", IsDark: true);
    private static readonly ThemePalette Light = new("#F6F7FB", "#FFFFFF", "#F1F3F9", "#E3E6EF", "#141A33", "#5B6385", "#2F5BEA", IsDark: false);

    private static MessageBody Body(string text = "", string? html = null) =>
        new(Guid.NewGuid(), [], [], text, [], html);

    [Fact]
    public void Politica_de_seguranca_bloqueia_scripts_e_rede_por_padrao()
    {
        var doc = EmailDocumentBuilder.Build(Body(html: "<p>oi</p>"), allowRemoteImages: false, Dark, EmailFonts.Default);

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

        var blocked = EmailDocumentBuilder.Build(Body(html: html), allowRemoteImages: false, Dark, EmailFonts.Default);
        var allowed = EmailDocumentBuilder.Build(Body(html: html), allowRemoteImages: true, Dark, EmailFonts.Default);

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
            allowRemoteImages: false, Dark, EmailFonts.Default);

        Assert.Contains("&lt;script&gt;", doc.Html);
        Assert.Contains("<a href=\"https://exemplo.com/a?b=1&amp;c=2\">", doc.Html);
        Assert.Contains("</a>. Fale", doc.Html); // ponto final fora do link
        Assert.Contains("<a href=\"mailto:ana@exemplo.com\">", doc.Html);
        Assert.Contains("class=\"plain\"", doc.Html);
    }

    [Fact]
    public void Email_simples_usa_as_cores_do_tema()
    {
        var doc = EmailDocumentBuilder.Build(Body(html: "<p>oi</p>"), false, Dark, EmailFonts.Default);
        Assert.Contains(".mail { background: #111833; color: #E8ECFF;", doc.Html);
        Assert.Contains("content=\"dark\"", doc.Html);
    }

    [Fact]
    public void Email_com_fundo_proprio_usa_a_propria_cor_no_tema_escuro()
    {
        // Tabela de largura total com fundo: a folha continua esse fundo (e não papel branco em volta).
        var doc = EmailDocumentBuilder.Build(Body(html: "<table bgcolor=\"#f4f4f4\"><tr><td>oferta</td></tr></table>"), false, Dark, EmailFonts.Default);
        Assert.Contains(".mail { background: #f4f4f4; color: #141A33;", doc.Html);
        Assert.Contains("content=\"light\"", doc.Html);
    }

    [Fact]
    public void Email_com_texto_escuro_fixado_fica_em_papel_no_tema_escuro()
    {
        // Ex.: Outlook com "color:windowtext" e sem fundo: preto sobre o fundo escuro ficaria ilegível.
        var doc = EmailDocumentBuilder.Build(Body(html: "<p class=\"MsoNormal\" style=\"color:windowtext\">oi</p>"), false, Dark, EmailFonts.Default);
        Assert.Contains(".mail { background: #FFFFFF; color: #141A33;", doc.Html);
    }

    [Fact]
    public void Todo_email_tem_a_mesma_folha_so_o_fundo_muda()
    {
        var tema = EmailDocumentBuilder.Build(Body(html: "<p>oi</p>"), false, Dark, EmailFonts.Default).Html;
        var papel = EmailDocumentBuilder.Build(Body(html: "<p style=\"color:#000\">oi</p>"), false, Dark, EmailFonts.Default).Html;
        foreach (var html in new[] { tema, papel })
            Assert.Contains("padding: 18px 20px; border-radius: 12px;", html);
    }

    [Fact]
    public void Folha_usa_o_fundo_externo_do_email()
    {
        var claro = EmailDocumentBuilder.Build(Body(html: "<html><body bgcolor=\"#f4f4f4\"><p>oi</p></body></html>"), false, Dark, EmailFonts.Default).Html;
        Assert.Contains(".mail { background: #f4f4f4; color: #141A33;", claro);
        Assert.Contains("border: 1px solid #f4f4f4;", claro); // a borda some no fundo do email

        // Email feito para fundo escuro: texto claro, mesmo com o app no tema claro.
        var escuro = EmailDocumentBuilder.Build(Body(html: "<html><body style=\"background-color:#101828\"><p>oi</p></body></html>"), false, Light, EmailFonts.Default).Html;
        Assert.Contains(".mail { background: #101828; color: #EDEFF7;", escuro);
    }

    [Fact]
    public void Texto_claro_fixado_continua_no_tema_escuro()
    {
        var doc = EmailDocumentBuilder.Build(Body(html: "<p style=\"color:#eeeeee\">oi</p>"), false, Dark, EmailFonts.Default);
        Assert.Contains(".mail { background: #111833; color: #E8ECFF;", doc.Html);
    }
}

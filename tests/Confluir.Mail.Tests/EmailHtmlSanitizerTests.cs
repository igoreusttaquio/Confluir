namespace Confluir.Mail.Tests;

public class EmailHtmlSanitizerTests
{
    private static SanitizedHtml S(string html, bool allowRemote = false) => EmailHtmlSanitizer.Sanitize(html, allowRemote);

    [Theory]
    [InlineData("<p>oi</p><script>alert(1)</script>", "alert")]
    [InlineData("<p onclick=\"roubar()\">oi</p>", "roubar")]
    [InlineData("<iframe src=\"https://exemplo.com\"></iframe><p>oi</p>", "iframe")]
    [InlineData("<form action=\"https://phishing.example\"><input name=\"senha\"></form>", "phishing")]
    [InlineData("<a href=\"javascript:roubar()\">clique</a>", "javascript")]
    [InlineData("<object data=\"x.swf\"></object>", "object")]
    [InlineData("<base href=\"https://outro.example/\"><p>oi</p>", "outro.example")]
    [InlineData("<img src=\"x\" onerror=\"roubar()\">", "onerror")]
    public void Remove_o_que_executa_codigo_ou_envia_dados(string html, string forbidden)
    {
        Assert.DoesNotContain(forbidden, S(html).Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bloqueia_e_conta_imagens_remotas_por_padrao()
    {
        const string html = """
            <p>Olá</p>
            <img src="https://rastreio.example/pixel.gif" width="1" height="1">
            <img src="http://cdn.example/banner.png">
            <img src="//cdn.example/sem-esquema.png">
            <img src="data:image/png;base64,iVBORw0KGgo=">
            """;

        var r = S(html);

        Assert.Equal(3, r.BlockedRemoteImages);
        Assert.DoesNotContain("rastreio.example", r.Html);
        Assert.DoesNotContain("cdn.example", r.Html);
        Assert.Contains("data:image/png;base64", r.Html); // imagens embutidas continuam
    }

    [Fact]
    public void Mostra_imagens_remotas_quando_permitido()
    {
        var r = S("<img src=\"https://cdn.example/banner.png\">", allowRemote: true);
        Assert.Equal(0, r.BlockedRemoteImages);
        Assert.Contains("https://cdn.example/banner.png", r.Html);
    }

    [Fact]
    public void Links_continuam_mesmo_com_imagens_bloqueadas()
    {
        var r = S("<a href=\"https://exemplo.com/pedido\">ver pedido</a>");
        Assert.Contains("https://exemplo.com/pedido", r.Html);
        Assert.Equal(0, r.BlockedRemoteImages);
    }

    [Fact]
    public void Preserva_estilos_do_head_e_layout_de_tabelas()
    {
        const string html = """
            <html><head><style>.titulo { color: #3d6bff; }</style></head>
            <body><table width="600" cellpadding="8" bgcolor="#ffffff"><tr><td class="titulo">Oferta</td></tr></table></body></html>
            """;

        var r = S(html);

        Assert.Contains(".titulo", r.Html);
        Assert.Contains("width=\"600\"", r.Html);
        Assert.Contains("class=\"titulo\"", r.Html);
        Assert.True(r.HasOwnBackground);
    }

    [Fact]
    public void Fundo_definido_no_body_e_preservado()
    {
        var r = S("<html><body style=\"background-color:#fafafa\"><p>oi</p></body></html>");
        Assert.Contains("background-color", r.Html);
        Assert.True(r.HasOwnBackground);
    }

    [Fact]
    public void Email_simples_nao_tem_fundo_proprio()
    {
        var r = S("<p>Oi <b>Ana</b>, tudo bem?</p>");
        Assert.False(r.HasOwnBackground);
        Assert.Contains("<b>Ana</b>", r.Html);
    }
}

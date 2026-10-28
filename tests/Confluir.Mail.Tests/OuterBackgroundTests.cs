namespace Confluir.Mail.Tests;

public sealed class OuterBackgroundTests
{
    private static string? Outer(string html) => EmailHtmlSanitizer.Sanitize(html, allowRemoteImages: false).OuterBackground;

    [Fact]
    public void Fundo_do_body_vale_para_o_email_inteiro() =>
        Assert.Equal("#f4f4f4", Outer("<html><body bgcolor=\"#F4F4F4\"><p>oi</p></body></html>"));

    [Fact]
    public void Tabela_invólucro_de_largura_total()
    {
        const string html = """
            <style>.x{color:red}</style>
            <table width="100%" style="background-color: rgb(244, 246, 251)"><tr><td align="center">
              <table width="600" bgcolor="#ffffff"><tr><td>conteúdo</td></tr></table>
            </td></tr></table>
            """;
        Assert.Equal("#f4f6fb", Outer(html));
    }

    [Fact]
    public void Desce_por_invólucros_sem_cor_até_achar_o_fundo() =>
        Assert.Equal("#101828", Outer("<center><div style=\"background:#101828 no-repeat\"><p>oi</p></div></center>"));

    [Theory]
    [InlineData("<table width=\"600\" bgcolor=\"#ffffff\"><tr><td>só a caixa de conteúdo</td></tr></table>")]
    [InlineData("<div style=\"background: url(x.png) #eee\"><p>imagem</p></div>")]
    [InlineData("<div style=\"background: linear-gradient(#fff, #000)\"><p>gradiente</p></div>")]
    [InlineData("<div style=\"background:#eee\">um</div><div style=\"background:#333\">dois</div>")]
    [InlineData("<p>sem fundo</p>")]
    public void Sem_cor_segura_fica_nulo(string html) => Assert.Null(Outer(html));

    [Theory]
    [InlineData("#FFF", "#ffffff")]
    [InlineData("rgb(16, 24, 40)", "#101828")]
    [InlineData("WhiteSmoke", "#f5f5f5")]
    [InlineData("transparent", null)]
    [InlineData("rgba(0,0,0,0)", null)]
    public void Normaliza_cores(string color, string? expected) => Assert.Equal(expected, EmailHtmlSanitizer.NormalizeColor(color));
}

namespace Confluir.Mail.Tests;

public sealed class DarkTextDetectionTests
{
    [Theory]
    [InlineData("<p style=\"color:#000\">oi</p>", true)]
    [InlineData("<p style=\"color: windowtext\">oi</p>", true)]          // Outlook
    [InlineData("<font color=\"#1f1f1f\">oi</font>", true)]
    [InlineData("<span style=\"color: rgb(34, 34, 34)\">oi</span>", true)]
    [InlineData("<span style=\"color: var(--x)\">oi</span>", true)]       // não reconhecida: na dúvida, escura
    [InlineData("<p style=\"color:#ffffff\">oi</p>", false)]
    [InlineData("<p style=\"color: inherit\">oi</p>", false)]
    [InlineData("<p style=\"background-color:#000\">oi</p>", false)]      // fundo não é cor de texto
    [InlineData("<p>sem cor</p>", false)]
    public void Detecta_cor_de_texto_escura(string html, bool dark) =>
        Assert.Equal(dark, EmailHtmlSanitizer.HasDarkTextColor(html));

    [Fact]
    public void Sanitizar_informa_o_texto_escuro()
    {
        Assert.True(EmailHtmlSanitizer.Sanitize("<div style=\"color:#333333\">Olá</div>", allowRemoteImages: false).HasDarkText);
        Assert.False(EmailHtmlSanitizer.Sanitize("<div>Olá</div>", allowRemoteImages: false).HasDarkText);
    }
}

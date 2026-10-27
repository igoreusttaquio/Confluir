using System.Globalization;
using Confluir.Core.Formatting;
using Confluir.Core.Localization;

namespace Confluir.Core.Tests;

public class LanguageResolverTests
{
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    [Theory]
    [InlineData(null, "pt-BR", "pt-BR", "pt-BR")]
    [InlineData(null, "pt-PT", "pt-BR", "pt-PT")]
    [InlineData(null, "en-GB", "en", "en-GB")]
    [InlineData(null, "es-MX", "es", "es-MX")]
    [InlineData(null, "de-DE", "en", "en-US")]   // idioma sem tradução → inglês
    [InlineData("", "fr-FR", "en", "en-US")]
    [InlineData("es", "pt-BR", "es", "es-ES")]   // escolha explícita vence o sistema
    [InlineData("EN", "en-AU", "en", "en-AU")]
    [InlineData("pt-BR", "en-US", "pt-BR", "pt-BR")]
    [InlineData("xx", "es-AR", "es", "es-AR")]   // código inválido → segue o sistema
    public void Resolve_idioma_e_cultura_de_formatacao(string? setting, string system, string code, string format)
    {
        var r = LanguageResolver.Resolve(setting, C(system));
        Assert.Equal(code, r.Code);
        Assert.Equal(code, r.UiCulture.Name);
        Assert.Equal(format, r.FormatCulture.Name);
    }

    [Fact]
    public void Datas_relativas_em_ingles_e_espanhol()
    {
        var brt = TimeSpan.FromHours(-3);
        var now = new DateTimeOffset(2026, 10, 29, 15, 0, 0, brt); // quinta
        var monday = new DateTimeOffset(2026, 10, 26, 9, 41, 0, brt);
        var october = new DateTimeOffset(2026, 10, 3, 9, 41, 0, brt);

        Assert.Equal("Mon", RelativeDateFormatter.Format(monday, now, C("en-US"), "Yesterday"));
        Assert.Equal("3 Oct", RelativeDateFormatter.Format(october, now, C("en-US"), "Yesterday"));
        Assert.Equal("Lun", RelativeDateFormatter.Format(monday, now, C("es-ES"), "Ayer"));
        Assert.Equal("3 oct", RelativeDateFormatter.Format(october, now, C("es-ES"), "Ayer"));
    }
}

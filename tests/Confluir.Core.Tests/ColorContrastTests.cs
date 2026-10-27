using Confluir.Core.Theming;

namespace Confluir.Core.Tests;

public class ColorContrastTests
{
    [Theory]
    [InlineData("#FFFFFF", "#000000", 21.0)]
    [InlineData("#000000", "#000000", 1.0)]
    [InlineData("#FFFFFF", "#777777", 4.48)]
    public void Calcula_razao_de_contraste_WCAG(string a, string b, double expected)
    {
        Assert.Equal(expected, ColorContrast.ContrastRatio(a, b), precision: 2);
    }

    [Theory]
    [InlineData("#2F5BEA", "#FFFFFF")] // Accent claro → texto branco
    [InlineData("#7B97FF", "#0B1020")] // Accent escuro → texto escuro
    [InlineData("#FFB938", "#0B1020")]
    [InlineData("#C8322A", "#FFFFFF")]
    public void Escolhe_texto_sobre_destaque(string accent, string expected)
    {
        Assert.Equal(expected, ColorContrast.PickForeground(accent, "#FFFFFF", "#0B1020"));
    }

    [Theory]
    [InlineData("#141A33", "#F6F7FB")]
    [InlineData("#5B6385", "#FFFFFF")]
    [InlineData("#E8ECFF", "#0B1020")]
    [InlineData("#9AA3C7", "#111833")]
    [InlineData("#FFFFFF", "#2F5BEA")]
    [InlineData("#0B1020", "#7B97FF")]
    public void Paleta_oficial_atende_WCAG_AA(string text, string background)
    {
        Assert.True(ColorContrast.ContrastRatio(text, background) >= 4.5);
    }

    [Theory]
    [InlineData("3d6bff")]
    [InlineData("#FF3D6BFF")]
    [InlineData(" #3D6BFF ")]
    public void Aceita_formatos_hex(string hex)
    {
        Assert.Equal(((byte)0x3D, (byte)0x6B, (byte)0xFF), ColorContrast.ParseHex(hex));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Rejeita_hex_invalido(string? hex)
    {
        Assert.False(ColorContrast.IsValidHex(hex));
    }
}

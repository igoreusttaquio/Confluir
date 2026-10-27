using System.Globalization;

namespace Confluir.Core.Theming;

/// <summary>Cálculos de contraste segundo a definição de luminância relativa do WCAG 2.</summary>
public static class ColorContrast
{
    public static (byte R, byte G, byte B) ParseHex(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8)
            s = s[2..]; // #AARRGGBB: o alfa não entra no contraste
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"Cor inválida: '{hex}'");
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static bool IsValidHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        try
        {
            ParseHex(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }

    public static double ContrastRatio(string a, string b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Escolhe, entre duas cores de texto, a que tem maior contraste sobre <paramref name="background"/>.</summary>
    public static string PickForeground(string background, string light, string dark) =>
        ContrastRatio(background, light) >= ContrastRatio(background, dark) ? light : dark;
}

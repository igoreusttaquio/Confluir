namespace Confluir.App.Services;

public static class ColorNames
{
    /// <summary>Chave de recurso do nome de uma cor de <see cref="Core.Models.AccountColors"/> (ex.: "Color_Blue").</summary>
    public static string KeyFor(string colorKey) => "Color_" + colorKey;
}

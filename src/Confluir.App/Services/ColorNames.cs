using Confluir.App.Resources;

namespace Confluir.App.Services;

public static class ColorNames
{
    /// <summary>Nome localizado de uma cor de <see cref="Core.Models.AccountColors"/>.</summary>
    public static string For(string key) => key switch
    {
        "Blue" => Strings.Color_Blue,
        "Coral" => Strings.Color_Coral,
        "Teal" => Strings.Color_Teal,
        "Amber" => Strings.Color_Amber,
        "Violet" => Strings.Color_Violet,
        "Pink" => Strings.Color_Pink,
        "Green" => Strings.Color_Green,
        "Cyan" => Strings.Color_Cyan,
        _ => key,
    };
}

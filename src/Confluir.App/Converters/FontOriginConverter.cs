using System.Globalization;
using Avalonia.Data.Converters;
using Confluir.App.Resources;
using Confluir.App.Services;

namespace Confluir.App.Converters;

/// <summary>Etiqueta da origem de uma fonte na lista ("Padrão", "Embutida", "Importada"); fontes do sistema ficam sem.</summary>
public static class FontOriginConverter
{
    public static readonly IValueConverter IsTagged =
        new FuncValueConverter<FontOrigin, bool>(origin => origin != FontOrigin.System);

    /// <summary>Valores: a origem e a versão do idioma (para retraduzir ao trocar de idioma).</summary>
    public static readonly IMultiValueConverter Label = new LabelConverter();

    private sealed class LabelConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Count > 0 && values[0] is FontOrigin origin
                ? origin switch
                {
                    FontOrigin.Default => Strings.Fonts_Origin_Default,
                    FontOrigin.Bundled => Strings.Fonts_Origin_Bundled,
                    FontOrigin.Imported => Strings.Fonts_Origin_Imported,
                    _ => "",
                }
                : "";
    }
}

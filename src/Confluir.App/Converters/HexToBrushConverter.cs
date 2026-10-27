using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Confluir.App.Converters;

/// <summary>Converte "#RRGGBB" em pincel. Usado para as cores de conta, que vêm dos dados e não do tema.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    private static readonly ConcurrentDictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color)
            ? Cache.GetOrAdd(hex, _ => new ImmutableSolidColorBrush(color))
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

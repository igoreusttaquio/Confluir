using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Confluir.App.Localization;

/// <summary>
/// Texto traduzido que se atualiza ao trocar de idioma: <c>Text="{l:Tr AddAccount_Title}"</c>.
/// Faz binding em <see cref="LocalizationService.Version"/> (renotificado a cada troca) e traduz a chave no conversor.
/// </summary>
public sealed class TrExtension(string key)
{
    public string Key { get; set; } = key;

    public object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding(nameof(LocalizationService.Version))
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            Converter = TrConverter.Instance,
            ConverterParameter = Key,
        };
}

internal sealed class TrConverter : IValueConverter
{
    public static readonly TrConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string key ? LocalizationService.Instance[key] : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Traduz uma chave vinda dos dados (ex.: nome de cor, tipo de segurança).
/// Uso em MultiBinding: a chave e <c>LocalizationService.Version</c>, para reavaliar ao trocar de idioma.
/// </summary>
public sealed class TrKeyConverter : IMultiValueConverter
{
    public static readonly TrKeyConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 0 && values[0] is string key ? LocalizationService.Instance[key] : null;
}

using System.Globalization;

namespace Confluir.Core.Formatting;

public static class RelativeDateFormatter
{
    /// <summary>
    /// Data curta para a lista de mensagens: hora (hoje), <paramref name="yesterdayLabel"/> (ontem),
    /// dia da semana (esta semana), dia e mês (este ano) ou data completa.
    /// </summary>
    public static string Format(DateTimeOffset date, DateTimeOffset now, CultureInfo culture, string yesterdayLabel)
    {
        var local = DateGrouping.SafeToOffset(date, now.Offset);
        return DateGrouping.GetGroup(date, now, culture) switch
        {
            DateGroup.Today => local.ToString("t", culture),
            DateGroup.Yesterday => yesterdayLabel,
            DateGroup.ThisWeek => culture.TextInfo.ToTitleCase(local.ToString("ddd", culture).TrimEnd('.')),
            _ when local.Year == now.Year => local.ToString("d MMM", culture).TrimEnd('.'),
            _ => local.ToString("d", culture),
        };
    }
}

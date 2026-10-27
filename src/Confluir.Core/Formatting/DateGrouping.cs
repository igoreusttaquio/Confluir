using System.Globalization;

namespace Confluir.Core.Formatting;

public enum DateGroup
{
    Today,
    Yesterday,
    ThisWeek,
    Older,
}

public static class DateGrouping
{
    /// <summary>
    /// Classifica a data de uma mensagem em relação a <paramref name="now"/>, no fuso de <paramref name="now"/>.
    /// Datas no futuro (relógio do servidor adiantado) contam como hoje.
    /// </summary>
    public static DateGroup GetGroup(DateTimeOffset date, DateTimeOffset now, DayOfWeek firstDayOfWeek)
    {
        var day = SafeToOffset(date, now.Offset).Date;
        var today = now.Date;

        if (day >= today)
            return DateGroup.Today;
        if (day == today.AddDays(-1))
            return DateGroup.Yesterday;

        var daysSinceWeekStart = ((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return day >= today.AddDays(-daysSinceWeekStart) ? DateGroup.ThisWeek : DateGroup.Older;
    }

    public static DateGroup GetGroup(DateTimeOffset date, DateTimeOffset now, CultureInfo culture) =>
        GetGroup(date, now, culture.DateTimeFormat.FirstDayOfWeek);

    /// <summary>
    /// Converte para outro fuso sem estourar nos extremos: datas a menos de um dia de
    /// <see cref="DateTimeOffset.MinValue"/>/<see cref="DateTimeOffset.MaxValue"/> (cabeçalhos inválidos)
    /// ficam presas ao limite em vez de lançar exceção.
    /// </summary>
    public static DateTimeOffset SafeToOffset(DateTimeOffset date, TimeSpan offset)
    {
        var utc = date.UtcDateTime;
        if (utc < DateTime.MinValue.AddDays(1))
            return new DateTimeOffset(DateTime.MinValue.AddDays(1).Ticks, TimeSpan.Zero).ToOffset(offset);
        if (utc > DateTime.MaxValue.AddDays(-1))
            return new DateTimeOffset(DateTime.MaxValue.AddDays(-1).Ticks, TimeSpan.Zero).ToOffset(offset);
        return date.ToOffset(offset);
    }
}

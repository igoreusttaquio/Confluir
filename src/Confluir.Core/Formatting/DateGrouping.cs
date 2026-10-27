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
        var day = date.ToOffset(now.Offset).Date;
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
}

using Confluir.App.Resources;
using Confluir.App.ViewModels;

namespace Confluir.App.Tests;

public sealed class SearchPeriodTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.FromHours(-3));
    private static readonly SearchPeriodOption Custom = new(SearchPeriodOption.CustomDays, nameof(Strings.Search_Period_Custom));

    private static DateTimeOffset LocalMidnight(int year, int month, int day)
    {
        var date = new DateTime(year, month, day);
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
    }

    private static (DateTimeOffset?, DateTimeOffset?) B(DateTimeOffset? since, DateTimeOffset? until) => (since, until);

    [Fact]
    public void Periodos_rapidos_continuam_contando_a_partir_de_agora()
    {
        Assert.Equal(B(null, null), MainWindowViewModel.PeriodBounds(new SearchPeriodOption(0, ""), null, null, Now));
        Assert.Equal(B(Now.AddDays(-7), null), MainWindowViewModel.PeriodBounds(new SearchPeriodOption(7, ""), null, null, Now));
        // As datas escolhidas antes não valem fora do personalizado.
        Assert.Equal(B(Now.AddDays(-30), null),
            MainWindowViewModel.PeriodBounds(new SearchPeriodOption(30, ""), new DateTime(2020, 1, 1), new DateTime(2020, 2, 1), Now));
    }

    [Fact]
    public void Personalizado_vai_do_comeco_do_primeiro_dia_ao_fim_do_ultimo()
    {
        var (since, until) = MainWindowViewModel.PeriodBounds(Custom, new DateTime(2026, 10, 1), new DateTime(2026, 10, 15), Now);
        Assert.Equal(LocalMidnight(2026, 10, 1), since);
        Assert.Equal(LocalMidnight(2026, 10, 16), until); // o dia 15 inteiro entra
    }

    [Fact]
    public void Personalizado_aceita_um_lado_so_e_datas_trocadas()
    {
        Assert.Equal(B(LocalMidnight(2026, 10, 1), null),
            MainWindowViewModel.PeriodBounds(Custom, new DateTime(2026, 10, 1), null, Now));
        Assert.Equal(B(null, LocalMidnight(2026, 10, 2)),
            MainWindowViewModel.PeriodBounds(Custom, null, new DateTime(2026, 10, 1), Now));
        Assert.Equal(B(LocalMidnight(2026, 10, 1), LocalMidnight(2026, 10, 16)),
            MainWindowViewModel.PeriodBounds(Custom, new DateTime(2026, 10, 15), new DateTime(2026, 10, 1), Now));
        Assert.Equal(B(null, null), MainWindowViewModel.PeriodBounds(Custom, null, null, Now));
    }
}

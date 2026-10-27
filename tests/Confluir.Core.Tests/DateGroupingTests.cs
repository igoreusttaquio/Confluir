using System.Globalization;
using Confluir.Core.Formatting;

namespace Confluir.Core.Tests;

public class DateGroupingTests
{
    private static readonly TimeSpan Brt = TimeSpan.FromHours(-3);

    // Quinta-feira, 29/10/2026 15:00 (BRT)
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, Brt);

    [Theory]
    [InlineData(2026, 10, 29, 0, 5, DateGroup.Today)]
    [InlineData(2026, 10, 30, 9, 0, DateGroup.Today)] // futuro
    [InlineData(2026, 10, 28, 23, 59, DateGroup.Yesterday)]
    [InlineData(2026, 10, 26, 8, 0, DateGroup.ThisWeek)]  // segunda
    [InlineData(2026, 10, 25, 8, 0, DateGroup.ThisWeek)]  // domingo, início da semana pt-BR
    [InlineData(2026, 10, 24, 23, 0, DateGroup.Older)]    // sábado anterior
    public void Classifica_em_relacao_a_agora(int y, int m, int d, int h, int min, DateGroup expected)
    {
        var date = new DateTimeOffset(y, m, d, h, min, 0, Brt);
        Assert.Equal(expected, DateGrouping.GetGroup(date, Now, DayOfWeek.Sunday));
    }

    [Fact]
    public void Converte_para_o_fuso_local_antes_de_comparar()
    {
        // 01:30 UTC do dia 29 = 22:30 do dia 28 em BRT
        var date = new DateTimeOffset(2026, 10, 29, 1, 30, 0, TimeSpan.Zero);
        Assert.Equal(DateGroup.Yesterday, DateGrouping.GetGroup(date, Now, DayOfWeek.Sunday));
    }

    [Fact]
    public void Ontem_tem_precedencia_quando_cai_na_semana_anterior()
    {
        var sunday = new DateTimeOffset(2026, 10, 25, 10, 0, 0, Brt);
        var saturday = new DateTimeOffset(2026, 10, 24, 10, 0, 0, Brt);
        Assert.Equal(DateGroup.Yesterday, DateGrouping.GetGroup(saturday, sunday, DayOfWeek.Sunday));
    }

    [Fact]
    public void Respeita_o_primeiro_dia_da_semana()
    {
        var sunday = new DateTimeOffset(2026, 10, 25, 10, 0, 0, Brt);
        Assert.Equal(DateGroup.ThisWeek, DateGrouping.GetGroup(sunday, Now, DayOfWeek.Sunday));
        Assert.Equal(DateGroup.Older, DateGrouping.GetGroup(sunday, Now, DayOfWeek.Monday));
    }

    [Fact]
    public void Formata_data_relativa_em_pt_BR()
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        string F(DateTimeOffset d) => RelativeDateFormatter.Format(d, Now, culture, "Ontem");

        Assert.Equal("09:41", F(new DateTimeOffset(2026, 10, 29, 9, 41, 0, Brt)));
        Assert.Equal("Ontem", F(new DateTimeOffset(2026, 10, 28, 9, 41, 0, Brt)));
        Assert.Equal("Seg", F(new DateTimeOffset(2026, 10, 26, 9, 41, 0, Brt)));
        Assert.Equal("3 out", F(new DateTimeOffset(2026, 10, 3, 9, 41, 0, Brt)));
        Assert.Equal("15/12/2025", F(new DateTimeOffset(2025, 12, 15, 9, 41, 0, Brt)));
    }
}

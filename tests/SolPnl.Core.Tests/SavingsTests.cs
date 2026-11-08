using SolPnl.Contracts.Models;
using SolPnl.Core.Site;

namespace SolPnl.Core.Tests;

public class SavingsPriceTests
{
    private static readonly IReadOnlyList<ElectricityRate> Rates =
    [
        new(null, 0.14, SummerPricePerKwh: 0.16),
        new(new DateOnly(2027, 1, 1), 0.15),
    ];

    [Theory]
    [InlineData(2026, 11, 8, 0.14)]   // first rate, winter
    [InlineData(2026, 7, 4, 0.16)]    // first rate, summer
    [InlineData(2027, 1, 1, 0.15)]    // new rate from its start date
    [InlineData(2027, 7, 4, 0.15)]    // new rate has no summer price
    public void PriceOn_UsesTheRateInEffect(int year, int month, int day, double expected) =>
        Assert.Equal(expected, SavingsService.PriceOn(Rates, new DateOnly(year, month, day)));

    [Fact]
    public void PriceOn_IsNull_BeforeTheFirstDatedRate() =>
        Assert.Null(SavingsService.PriceOn([new ElectricityRate(new DateOnly(2027, 1, 1), 0.15)], new DateOnly(2026, 12, 31)));

    [Fact]
    public void Saved_IsSolarTimesPrice_RoundedToCents()
    {
        var day = new DaySummary(new DateOnly(2026, 11, 8), null, null, null, null, null, null, null, null, 23.456, 30, 7);
        Assert.Equal(3.28, SavingsService.Saved(Rates, day)); // 23.456 kWh × $0.14
        Assert.Null(SavingsService.Saved(Rates, day with { SolarKwh = null }));
        Assert.Null(SavingsService.Saved([], day));
    }
}

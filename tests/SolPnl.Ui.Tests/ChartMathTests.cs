using System.Globalization;
using SolPnl.Contracts.Models;
using SolPnl.Ui.Charts;
using SolPnl.Ui.Services;

namespace SolPnl.Ui.Tests;

public class ChartMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 283, 0, 300, 100)]
    [InlineData(0, 1514, 0, 2000, 500)]
    [InlineData(121.9, 122.6, 121.8, 122.6, 0.2)]
    [InlineData(43.1, 45.4, 43, 46, 1)]
    public void NiceScale_CoversDataWithRoundSteps(double min, double max, double low, double high, double step)
    {
        var (l, h, ticks) = ChartMath.NiceScale(min, max);
        Assert.Equal(low, l, 6);
        Assert.Equal(high, h, 6);
        Assert.Equal(step, ticks[1] - ticks[0], 6);
        Assert.True(l <= min && h >= max);
    }

    [Fact]
    public void NiceScale_FlatSeries_StillHasARange()
    {
        var (low, high, ticks) = ChartMath.NiceScale(0, 0);
        Assert.True(high > low);
        Assert.True(low >= 0);
        Assert.True(ticks.Count >= 2);
    }

    [Fact]
    public void Segments_BreakAtGaps()
    {
        var points = new[] { 0, 10, 20, 30, 300, 310, 320 }
            .Select(s => new HistoryPoint(T0.AddSeconds(s), 1)).ToList();
        var segments = ChartMath.Segments(points);
        Assert.Equal([4, 3], segments.Select(s => s.Count));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(14, 1)]
    [InlineData(16, 2)]
    [InlineData(1000, 3)]
    [InlineData(-50, 0)]
    public void Nearest_FindsClosestPoint(int secondsAfterT0, int expected)
    {
        var points = new[] { 0, 10, 20, 30 }.Select(s => new HistoryPoint(T0.AddSeconds(s), s)).ToList();
        Assert.Equal(expected, ChartMath.Nearest(points, T0.AddSeconds(secondsAfterT0)));
    }

    [Fact]
    public void TimeTicks_AlignToRoundLocalTimes_AndStayInRange()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC-6", TimeSpan.FromHours(-6), "UTC-6", "UTC-6");
        var from = new DateTimeOffset(2026, 11, 7, 13, 7, 0, TimeSpan.FromHours(-6));
        var ticks = ChartMath.TimeTicks(from, from.AddHours(1), 400, zone, CultureInfo.InvariantCulture);

        Assert.NotEmpty(ticks);
        Assert.All(ticks, t => Assert.InRange(t.Value, from, from.AddHours(1)));
        Assert.All(ticks, t => Assert.Equal(0, TimeZoneInfo.ConvertTime(t.Value, zone).Minute % 15));
        Assert.Equal("13:15", ticks[0].Label);
    }

    [Fact]
    public void TimeTicks_MultiDay_UsesDates()
    {
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var ticks = ChartMath.TimeTicks(from, from.AddDays(7), 700, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
        Assert.Contains(ticks, t => t.Label == "Nov 2");
    }
}

public class MetricFormatTests
{
    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(0.25, 2)]
    [InlineData(5, 0)]
    [InlineData(100, 0)]
    [InlineData(0.05, 2)]
    public void DecimalsFor_MatchesStep(double step, int expected) =>
        Assert.Equal(expected, MetricFormat.DecimalsFor(step));

    [Fact]
    public void Tick_NeighbouringTicksAreDistinct()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(["121.8", "122.0", "122.2"], new[] { 121.8, 122.0, 122.2 }.Select(v => MetricFormat.Tick(v, 0.2)));
        Assert.Equal("2,000", MetricFormat.Tick(2000, 500));
        Assert.Equal("20K", MetricFormat.Tick(20_000, 10_000));
    }

    [Theory]
    [InlineData("pv.power", "Solar power")]
    [InlineData("pv.power.2", "Solar power · string 2")]
    [InlineData("load.power.l1", "Load · L1")]
    [InlineData("inverter.temperature.3", "Inverter temperature · sensor 3")]
    [InlineData("vendor.other.thing", "vendor.other.thing")]
    public void DisplayName_IsReadable(string metric, string expected) =>
        Assert.Equal(expected, MetricFormat.DisplayName(metric));

    [Fact]
    public void Format_UsesUnitPrecision()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("1,514 W", MetricFormat.Format(1514, "W"));
        Assert.Equal("122.6 V", MetricFormat.Format(122.6, "V"));
        Assert.Equal("59.99 Hz", MetricFormat.Format(59.99, "Hz"));
        Assert.Equal("87%", MetricFormat.Format(87, "%"));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}

public class PressureUnitTests
{
    [Fact]
    public void Pressure_FollowsTheUnitChoice()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUnit = MetricFormat.TemperatureUnit;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            MetricFormat.TemperatureUnit = SolPnl.Contracts.Models.TemperatureUnits.Fahrenheit;
            Assert.Equal("30.09 inHg", MetricFormat.Pressure(1019.1));
            Assert.Equal("inHg", MetricFormat.ToDisplay(1019.1, "hPa").Unit);

            MetricFormat.TemperatureUnit = SolPnl.Contracts.Models.TemperatureUnits.Celsius;
            Assert.Equal("1,019 hPa", MetricFormat.Pressure(1019.1));
            Assert.Equal("–", MetricFormat.Pressure(null));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            MetricFormat.TemperatureUnit = previousUnit;
        }
    }
}

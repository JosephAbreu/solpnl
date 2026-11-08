using SolPnl.Contracts.Models;
using SolPnl.Core.Site;

namespace SolPnl.Core.Tests;

public class SunCalculatorTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void Austin_MatchesOpenMeteoWithinTwoMinutes()
    {
        // Open-Meteo, 2026-11-07, 30.27 N 97.74 W: sunrise 12:50:09Z, sunset 23:39:01Z.
        var sun = SunCalculator.Calculate(new DateOnly(2026, 11, 7), 30.27, -97.74, Chicago);
        Assert.InRange((sun.Sunrise!.Value - DateTimeOffset.FromUnixTimeSeconds(1794055809)).Duration().TotalMinutes, 0, 2);
        Assert.InRange((sun.Sunset!.Value - DateTimeOffset.FromUnixTimeSeconds(1794094741)).Duration().TotalMinutes, 0, 2);
        Assert.Equal(TimeSpan.FromHours(-6), sun.Sunrise.Value.Offset); // local time, CST
        Assert.True(sun.CivilDawn < sun.Sunrise && sun.CivilDusk > sun.Sunset);
        Assert.True(sun.SolarNoon > sun.Sunrise && sun.SolarNoon < sun.Sunset);
        Assert.InRange(sun.DayLength.TotalHours, 10.5, 11);
    }

    [Fact]
    public void Polar_DayAndNight()
    {
        var utc = TimeZoneInfo.Utc;
        var midsummer = SunCalculator.Calculate(new DateOnly(2026, 6, 21), 78.2, 15.6, utc); // Svalbard
        Assert.Null(midsummer.Sunrise);
        Assert.Equal(TimeSpan.FromHours(24), midsummer.DayLength);

        var midwinter = SunCalculator.Calculate(new DateOnly(2026, 12, 21), 78.2, 15.6, utc);
        Assert.Null(midwinter.Sunset);
        Assert.Equal(TimeSpan.Zero, midwinter.DayLength);
    }

    [Fact]
    public void Elevation_HighAtNoon_NegativeAtMidnight()
    {
        var sun = SunCalculator.Calculate(new DateOnly(2026, 11, 7), 30.27, -97.74, Chicago);
        Assert.InRange(SunCalculator.ElevationDegrees(sun.SolarNoon, 30.27, -97.74), 40, 45); // ~90 - 30.3 - 16.4 (declination)
        Assert.True(SunCalculator.ElevationDegrees(sun.SolarNoon.AddHours(12), 30.27, -97.74) < 0);
    }

    [Fact]
    public void ClearSkyIrradiation_IsPlausible()
    {
        var november = SunCalculator.ClearSkyIrradiation(new DateOnly(2026, 11, 7), 30.27, -97.74, Chicago);
        var june = SunCalculator.ClearSkyIrradiation(new DateOnly(2026, 6, 21), 30.27, -97.74, Chicago);
        Assert.InRange(november, 4, 6.5);   // kWh/m² on a clear November day in Texas
        Assert.InRange(june, 7, 9.5);
        // Open-Meteo forecast that (cloudy) day: 9.68 MJ/m² = 2.69 kWh/m², about half of clear-sky.
        Assert.True(9.68 / 3.6 < november);
    }
}

public class SiteSettingsTests
{
    [Fact]
    public void Validate_NormalizesAndRejects()
    {
        var ok = SiteSettingsStore.Validate(new SiteLocation(" Home ", 30.267153, -97.743061, 149.04, "America/Chicago"));
        Assert.Equal("Home", ok.Name);
        Assert.Equal(30.26715, ok.Latitude);
        Assert.Equal(149.0, ok.ElevationMeters);

        Assert.Throws<ArgumentException>(() => SiteSettingsStore.Validate(new SiteLocation(null, 91, 0, null, "UTC")));
        Assert.Throws<ArgumentException>(() => SiteSettingsStore.Validate(new SiteLocation(null, 0, -181, null, "UTC")));
        Assert.Throws<ArgumentException>(() => SiteSettingsStore.Validate(new SiteLocation(null, 0, 0, null, "Mars/Olympus")));
        Assert.Throws<ArgumentException>(() => SiteSettingsStore.Validate(new SiteLocation(null, 0, 0, 20000, "UTC")));

        // Blank time zone falls back to the machine's.
        Assert.False(string.IsNullOrEmpty(SiteSettingsStore.Validate(new SiteLocation(null, 0, 0, null, "")).TimeZoneId));
    }
}

public class InsightCalculatorTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly DateOnly Today = new(2026, 11, 7);

    private static SunTimes Sun(DateOnly d) => SunCalculator.Calculate(d, 30.27, -97.74, Chicago);

    private static DaySummary Day(int daysAgo, double? forecastIrradiation, double? solarKwh) =>
        new(Today.AddDays(-daysAgo), null, null, null, null, forecastIrradiation, 5, null, null, solarKwh, null, null);

    /// <summary>Hourly irradiance for a day: a sine curve between 7 AM and 6 PM local, peaking at <paramref name="peak"/>.</summary>
    private static List<HourlyForecast> Hours(DateOnly date, double peak) =>
        Enumerable.Range(0, 24).Select(h =>
        {
            var local = date.ToDateTime(new TimeOnly(h, 0));
            var t = new DateTimeOffset(local, Chicago.GetUtcOffset(local));
            var x = (h - 7) / 11.0;
            var ghi = x is > 0 and < 1 ? peak * Math.Sin(x * Math.PI) : 0;
            return new HourlyForecast(t.ToUniversalTime(), 20, 10, ghi, 0);
        }).ToList();

    private static InsightCalculator.Inputs Inputs(DateTimeOffset now, IReadOnlyList<DaySummary> past, double? tomorrowIrradiation = 4,
        double peak = 700) =>
        new(now, Chicago, Sun(Today), Sun(Today.AddDays(1)),
            [.. Hours(Today, peak), .. Hours(Today.AddDays(1), peak)],
            new DailyForecast(Today.AddDays(1), 20, 10, tomorrowIrradiation, 10, 1),
            5.0, past);

    private static DateTimeOffset LocalTime(int hour) =>
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(-6));

    private static readonly List<DaySummary> SteadyHistory =
        Enumerable.Range(1, 10).Select(i => Day(i, 4.0, 20.0)).ToList(); // 5 kWh per kWh/m²

    [Fact]
    public void SolarFactor_NeedsThreeQualifyingDays()
    {
        Assert.Null(InsightCalculator.SolarFactor([Day(1, 4, 20), Day(2, 4, 20)]));
        Assert.Null(InsightCalculator.SolarFactor([Day(1, 1.0, 5), Day(2, 1.0, 5), Day(3, 1.0, 5)])); // too cloudy to learn from
        Assert.Equal(5, InsightCalculator.SolarFactor([Day(1, 4, 20), Day(2, 4, 24), Day(3, 4, 16)]));
    }

    [Fact]
    public void Outlook_EstimatesKwhOnceLearned()
    {
        var insight = InsightCalculator.Compute(Inputs(LocalTime(9), SteadyHistory, tomorrowIrradiation: 4))
            .Single(i => i.Kind == InsightKinds.SolarOutlook);
        Assert.Equal("Sunny tomorrow", insight.Title);          // 4 of 5 kWh/m² clear-sky = 80%
        Assert.Contains("about 20 kWh", insight.Message);       // 5 x 4
        Assert.Contains("80%", insight.Message);
        Assert.Equal(InsightLevels.Good, insight.Level);
    }

    [Fact]
    public void Outlook_WhileLearning_ExplainsWhy()
    {
        var insight = InsightCalculator.Compute(Inputs(LocalTime(9), [Day(1, 4, 20)], tomorrowIrradiation: 1))
            .Single(i => i.Kind == InsightKinds.SolarOutlook);
        Assert.Equal("Cloudy tomorrow", insight.Title);
        Assert.Contains("1 so far", insight.Message);
    }

    [Fact]
    public void BestTime_TodayFindsTheMiddayWindow()
    {
        var insight = InsightCalculator.Compute(Inputs(LocalTime(8), SteadyHistory)).Single(i => i.Kind == InsightKinds.BestTime);
        Assert.Contains("today", insight.Title);
        Assert.Contains("11 AM", insight.Title);  // irradiance peaks at 12:30; the best 3-hour block is 11 AM-2 PM
        Assert.Contains("2 PM", insight.Title);
        Assert.Contains("kW of solar", insight.Message);
    }

    [Fact]
    public void BestTime_AfterSunset_LooksAtTomorrow_AndFlagsLittleSun()
    {
        var evening = InsightCalculator.Compute(Inputs(LocalTime(20), SteadyHistory)).Single(i => i.Kind == InsightKinds.BestTime);
        Assert.Contains("tomorrow", evening.Title);

        var dim = InsightCalculator.Compute(Inputs(LocalTime(20), SteadyHistory, peak: 100)).Single(i => i.Kind == InsightKinds.BestTime);
        Assert.Equal("Little sun tomorrow", dim.Title);
    }

    [Fact]
    public void Performance_WarnsAfterTwoLowDays_NotOne()
    {
        var twoLow = new List<DaySummary> { Day(1, 4, 10), Day(2, 4, 11) };
        twoLow.AddRange(Enumerable.Range(3, 8).Select(i => Day(i, 4, 20)));
        var warning = InsightCalculator.Compute(Inputs(LocalTime(9), twoLow)).Single(i => i.Kind == InsightKinds.Underperformance);
        Assert.Equal(InsightLevels.Warning, warning.Level);
        Assert.Contains("50%", warning.Message);

        var oneLow = new List<DaySummary> { Day(1, 4, 10) };
        oneLow.AddRange(Enumerable.Range(2, 8).Select(i => Day(i, 4, 20)));
        Assert.Equal(InsightLevels.Info,
            InsightCalculator.Compute(Inputs(LocalTime(9), oneLow)).Single(i => i.Kind == InsightKinds.Underperformance).Level);

        Assert.Equal(InsightLevels.Good,
            InsightCalculator.Compute(Inputs(LocalTime(9), SteadyHistory)).Single(i => i.Kind == InsightKinds.Underperformance).Level);
    }

    [Fact]
    public void Performance_NeedsEnoughSunnyDays()
    {
        Assert.DoesNotContain(InsightCalculator.Compute(Inputs(LocalTime(9), SteadyHistory.Take(4).ToList())),
            i => i.Kind == InsightKinds.Underperformance);
    }
}

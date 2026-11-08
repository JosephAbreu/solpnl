using System.Globalization;
using SolPnl.Contracts.Models;

namespace SolPnl.Core.Site;

/// <summary>
/// Turns forecasts, sun times and history into recommendations. Pure and deterministic, so it can be unit-tested.
///
/// The key number is the system's learned "solar factor": kWh produced per kWh/m² of forecast sunlight (effectively
/// the array's area times its efficiency, including tilt and shading). It's the median over recent days, so it adapts
/// to the season and to the actual installation without any panel specs being entered.
/// </summary>
public static class InsightCalculator
{
    /// <summary>Days with less forecast sunlight than this (kWh/m²) are too cloudy to judge performance by.</summary>
    public const double MinUsefulIrradiation = 1.5;

    public const int MinDaysToLearn = 3;
    public const int MinDaysForPerformanceCheck = 6;
    public const double UnderperformanceRatio = 0.75;
    private const int LearningWindowDays = 14;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public sealed record Inputs(
        DateTimeOffset Now,
        TimeZoneInfo Zone,
        SunTimes Today,
        SunTimes Tomorrow,
        IReadOnlyList<HourlyForecast> Hourly,
        DailyForecast? TomorrowForecast,
        double TomorrowClearSkyIrradiation,
        IReadOnlyList<DaySummary> PastDays);

    public static IReadOnlyList<Insight> Compute(Inputs inputs)
    {
        var insights = new List<Insight>();
        var factor = SolarFactor(inputs.PastDays);

        if (SolarOutlook(inputs, factor) is { } outlook) insights.Add(outlook);
        if (BestTimeForHeavyLoads(inputs, factor) is { } best) insights.Add(best);
        if (Performance(inputs.PastDays) is { } performance) insights.Add(performance);
        return insights;
    }

    /// <summary>Median kWh per kWh/m² over recent days with useful sunlight and solar data, or null while learning.</summary>
    public static double? SolarFactor(IReadOnlyList<DaySummary> pastDays)
    {
        var ratios = Qualifying(pastDays).Take(LearningWindowDays).Select(d => d.SolarKwh!.Value / d.ForecastIrradiationKwhM2!.Value).ToList();
        return ratios.Count >= MinDaysToLearn ? Median(ratios) : null;
    }

    private static Insight? SolarOutlook(Inputs inputs, double? factor)
    {
        if (inputs.TomorrowForecast?.IrradiationKwhM2 is not { } forecast || inputs.TomorrowClearSkyIrradiation <= 0) return null;

        var share = Math.Clamp(forecast / inputs.TomorrowClearSkyIrradiation, 0, 1.2);
        var title = share switch
        {
            >= 0.75 => "Sunny tomorrow",
            >= 0.4 => "Partly sunny tomorrow",
            _ => "Cloudy tomorrow",
        };
        var level = share >= 0.75 ? InsightLevels.Good : InsightLevels.Info;
        var clearDay = S($"{Math.Min(share, 1):P0} of a clear day's sunlight");

        var message = factor is { } k
            ? $"Expect about {Kwh(k * forecast)} of solar tomorrow ({clearDay})."
            : $"Tomorrow's forecast is {clearDay}. SolPnl will estimate kWh once it has {MinDaysToLearn} sunny days of history " +
              $"({Qualifying(inputs.PastDays).Count()} so far).";
        return new Insight(InsightKinds.SolarOutlook, level, title, message);
    }

    private static Insight? BestTimeForHeavyLoads(Inputs inputs, double? factor)
    {
        // Today if there are at least 2 daylight hours left, otherwise tomorrow.
        var useToday = inputs.Today.Sunset is { } todaySet && todaySet - inputs.Now >= TimeSpan.FromHours(2);
        var sun = useToday ? inputs.Today : inputs.Tomorrow;
        if (sun.Sunrise is not { } rise || sun.Sunset is not { } set) return null;

        var start = useToday ? Max(rise, inputs.Now) : rise;
        var hours = inputs.Hourly
            .Where(h => h.IrradianceWm2 is not null && h.Time >= start.AddMinutes(-30) && h.Time <= set)
            .OrderBy(h => h.Time)
            .ToList();
        if (hours.Count == 0) return null;

        const int window = 3;
        var bestIndex = 0;
        var bestSum = double.MinValue;
        for (var i = 0; i + Math.Min(window, hours.Count) <= hours.Count; i++)
        {
            var sum = hours.Skip(i).Take(window).Sum(h => h.IrradianceWm2!.Value);
            if (sum > bestSum)
            {
                bestSum = sum;
                bestIndex = i;
            }
        }

        var span = hours.Skip(bestIndex).Take(window).ToList();
        var average = span.Average(h => h.IrradianceWm2!.Value);
        var day = useToday ? "today" : "tomorrow";

        if (average < 150)
            return new Insight(InsightKinds.BestTime, InsightLevels.Info, $"Little sun {day}",
                $"Not much sunlight is forecast {day}, so heavy loads will mostly run on grid power whenever you run them.");

        // Open-Meteo's hourly irradiance is the mean of the preceding hour, so the window starts an hour before the first entry.
        var from = TimeZoneInfo.ConvertTime(span[0].Time.AddHours(-1), inputs.Zone);
        var to = TimeZoneInfo.ConvertTime(span[^1].Time, inputs.Zone);
        var power = factor is { } k ? S($" (about {k * average / 1000:0.0} kW of solar)") : "";
        return new Insight(InsightKinds.BestTime, InsightLevels.Good, $"Best time for heavy loads {day}: {Hour(from)}–{Hour(to)}",
            $"The sunniest stretch {day} is {Hour(from)} to {Hour(to)}{power}. Run laundry, the dishwasher or EV charging then " +
            "to use solar instead of grid power.");
    }

    private static Insight? Performance(IReadOnlyList<DaySummary> pastDays)
    {
        var days = Qualifying(pastDays).ToList(); // newest first
        if (days.Count < MinDaysForPerformanceCheck) return null;

        var baseline = Median(days.Skip(2).Take(LearningWindowDays).Select(Ratio).ToList());
        var latest = Ratio(days[0]) / baseline;
        var previous = Ratio(days[1]) / baseline;

        if (latest < UnderperformanceRatio && previous < UnderperformanceRatio)
            return new Insight(InsightKinds.Underperformance, InsightLevels.Warning, "Solar below forecast",
                S($"Solar output was {1 - latest:P0} and {1 - previous:P0} below what the weather forecast predicted on the last two ") +
                $"sunny days ({Day(days[0].Date)}, {Day(days[1].Date)}). Check the panels for dirt or new shading, and both PV strings for a fault.");

        if (latest < UnderperformanceRatio)
            return new Insight(InsightKinds.Underperformance, InsightLevels.Info, "Solar a bit below forecast",
                S($"On {Day(days[0].Date)} solar output was {1 - latest:P0} below what the forecast predicted. One day like this is ") +
                "usually the forecast being off; SolPnl will flag it if it continues.");

        return new Insight(InsightKinds.Underperformance, InsightLevels.Good, "Solar performing as expected",
            S($"Recent solar output matches what the weather forecasts predicted ({latest:P0} of the usual on {Day(days[0].Date)})."));
    }

    /// <summary>Past days (newest first) with enough forecast sunlight and recorded solar energy to learn from.</summary>
    private static IEnumerable<DaySummary> Qualifying(IReadOnlyList<DaySummary> pastDays) =>
        pastDays
            .Where(d => d.ForecastIrradiationKwhM2 >= MinUsefulIrradiation && d.SolarKwh > 0.2)
            .OrderByDescending(d => d.Date);

    private static double Ratio(DaySummary d) => d.SolarKwh!.Value / d.ForecastIrradiationKwhM2!.Value;

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static string Kwh(double kwh) => kwh >= 10 ? S($"{kwh:0} kWh") : S($"{kwh:0.0} kWh");

    /// <summary>Formats numbers and percentages the same way whatever the server's culture is.</summary>
    private static string S(FormattableString text) => text.ToString(Culture);
    private static string Hour(DateTimeOffset t) => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", Culture);
    private static string Day(DateOnly d) => d.ToString("MMM d", Culture);
}

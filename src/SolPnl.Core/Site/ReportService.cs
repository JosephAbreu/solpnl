using SolPnl.Contracts.Models;

namespace SolPnl.Core.Site;

/// <summary>Energy and money totals by year, month or day, from the daily summaries, compared with a year earlier.</summary>
public sealed class ReportService(DaySummaryStore days, SavingsService savings, SiteSettingsStore site, TimeProvider time)
{
    /// <summary>
    /// No <paramref name="year"/>: one period per year. A year: one per month. A year and <paramref name="month"/>:
    /// one per day. Throws <see cref="ArgumentException"/> for a month without a year, or out-of-range values.
    /// </summary>
    public async Task<EnergyReport> GetAsync(int? year, int? month, CancellationToken ct = default)
    {
        if (month is not null && year is null) throw new ArgumentException("A month needs a year.");
        if (year is < 2000 or > 2100) throw new ArgumentException("Year must be between 2000 and 2100.");
        if (month is < 1 or > 12) throw new ArgumentException("Month must be between 1 and 12.");

        var settings = await savings.GetSettingsAsync(ct);
        var rates = settings.RateList;
        var location = await site.GetLocationAsync(ct);
        var zone = location is null ? TimeZoneInfo.Local : SiteSettingsStore.Zone(location);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);

        var all = (await days.GetRangeAsync(DateOnly.MinValue, today, ct))
            .Where(HasEnergy)
            .Select(d => rates.Count == 0 ? d : d with { Saved = SavingsService.Saved(rates, d) })
            .ToList();
        var years = all.Select(d => d.Date.Year).Append(today.Year).Distinct().Order().ToList();
        var currency = rates.Count == 0 ? null : settings.Currency;

        IEnumerable<DaySummary> Between(DateOnly from, DateOnly to) => all.Where(d => d.Date >= from && d.Date <= to);

        if (year is not { } y)
        {
            var periods = years.Select(py => Aggregate(new DateOnly(py, 1, 1), all.Where(d => d.Date.Year == py))).ToList();
            var first = all.Count > 0 ? all[0].Date : today;
            return new EnergyReport(ReportScopes.All, null, null, currency, Aggregate(first, all), null, periods, [],
                Best(all), years, today);
        }

        if (month is not { } m)
        {
            var start = new DateOnly(y, 1, 1);
            var end = new DateOnly(y, 12, 31);
            var periods = Enumerable.Range(1, 12)
                .Select(pm => Aggregate(new DateOnly(y, pm, 1), all.Where(d => d.Date.Year == y && d.Date.Month == pm)))
                .ToList();
            var previousPeriods = Enumerable.Range(1, 12)
                .Select(pm => Aggregate(new DateOnly(y - 1, pm, 1), all.Where(d => d.Date.Year == y - 1 && d.Date.Month == pm)))
                .Select(p => p.Days > 0 ? p : null)
                .ToList();
            var inYear = Between(start, end).ToList();
            return new EnergyReport(ReportScopes.Year, y, null, currency, Aggregate(start, inYear),
                PreviousOrNull(start, end, today, Between), periods, previousPeriods, Best(inYear), years, today);
        }

        {
            var start = new DateOnly(y, m, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var inMonth = Between(start, end).ToList();
            var periods = Enumerable.Range(0, end.Day)
                .Select(i => start.AddDays(i))
                .Select(day => Aggregate(day, inMonth.Where(d => d.Date == day)))
                .ToList();
            return new EnergyReport(ReportScopes.Month, y, m, currency, Aggregate(start, inMonth),
                PreviousOrNull(start, end, today, Between), periods, [], Best(inMonth), years, today);
        }
    }

    /// <summary>The same period a year earlier; for a period that includes today, only up to the same day.</summary>
    private static EnergyPeriod? PreviousOrNull(DateOnly start, DateOnly end, DateOnly today,
        Func<DateOnly, DateOnly, IEnumerable<DaySummary>> between)
    {
        var to = today >= start && today <= end ? today : end;
        var previous = Aggregate(start.AddYears(-1), between(start.AddYears(-1), to.AddYears(-1)));
        return previous.Days > 0 ? previous : null;
    }

    private static bool HasEnergy(DaySummary d) => d.SolarKwh is not null || d.LoadKwh is not null || d.GridKwh is not null;

    private static DaySummary? Best(IEnumerable<DaySummary> list) =>
        list.Where(d => d.SolarKwh is > 0).OrderByDescending(d => d.SolarKwh).ThenBy(d => d.Date).FirstOrDefault();

    public static EnergyPeriod Aggregate(DateOnly start, IEnumerable<DaySummary> list)
    {
        var items = list.Where(HasEnergy).ToList();
        static double? Sum(IEnumerable<double?> values, int digits)
        {
            var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            return present.Count == 0 ? null : Math.Round(present.Sum(), digits);
        }

        // Self-powered share over the days that recorded both, so a day missing one side doesn't skew it.
        var both = items.Where(d => d.SolarKwh is not null && d.LoadKwh is not null).ToList();
        var bothLoad = both.Sum(d => d.LoadKwh!.Value);
        double? selfPowered = bothLoad > 0
            ? Math.Round(Math.Min(100, both.Sum(d => d.SolarKwh!.Value) / bothLoad * 100), 1)
            : null;

        return new EnergyPeriod(start, items.Count,
            Sum(items.Select(d => d.SolarKwh), 2),
            Sum(items.Select(d => d.LoadKwh), 2),
            Sum(items.Select(d => d.GridKwh), 2),
            Sum(items.Select(d => d.Saved), 2),
            selfPowered);
    }
}

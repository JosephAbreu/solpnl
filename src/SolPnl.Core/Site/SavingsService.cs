using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;

namespace SolPnl.Core.Site;

/// <summary>
/// Money saved by solar: each day's solar energy (from the daily summaries) times the grid price in effect that day.
/// Every solar kWh is assumed to be used at home rather than exported (true for systems that don't export).
/// </summary>
public sealed class SavingsService(IDbContextFactory<SolPnlDb> dbFactory, DaySummaryStore days, SiteSettingsStore site, TimeProvider time)
{
    public const string SettingsKey = "savings.settings";

    public async Task<SavingsSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.AppSettings.AsNoTracking().Where(s => s.Key == SettingsKey).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return json is null ? new SavingsSettings() : JsonSerializer.Deserialize<SavingsSettings>(json) ?? new SavingsSettings();
    }

    /// <summary>Validates and saves. Rates are kept in date order; one rate per start date. Throws <see cref="ArgumentException"/>.</summary>
    public async Task<SavingsSettings> SaveSettingsAsync(SavingsSettings settings, CancellationToken ct = default)
    {
        var currency = string.IsNullOrWhiteSpace(settings.Currency) ? "$" : settings.Currency.Trim();
        if (currency.Length > 5) throw new ArgumentException("Use a short currency symbol, such as $ or €.");
        foreach (var rate in settings.RateList)
        {
            if (rate.PricePerKwh is <= 0 or > 10 || rate.SummerPricePerKwh is <= 0 or > 10)
                throw new ArgumentException("Prices must be per kWh, more than 0 and at most 10 (e.g. 0.15).");
        }
        if (settings.SystemCost is < 0 or > 10_000_000) throw new ArgumentException("System cost must be a positive amount.");
        var rates = settings.RateList
            .GroupBy(r => r.From)
            .Select(g => g.Last())
            .OrderBy(r => r.From ?? DateOnly.MinValue)
            .ToList();

        var saved = new SavingsSettings(currency, rates, settings.SystemCost);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == SettingsKey, ct);
        var json = JsonSerializer.Serialize(saved);
        if (row is null) db.AppSettings.Add(new AppSettingEntity { Key = SettingsKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
        return saved;
    }

    /// <summary>Price per kWh on <paramref name="date"/>, or null if no rate covers it.</summary>
    public static double? PriceOn(IReadOnlyList<ElectricityRate> rates, DateOnly date)
    {
        var rate = rates.Where(r => r.From is null || r.From <= date).OrderBy(r => r.From ?? DateOnly.MinValue).LastOrDefault();
        if (rate is null) return null;
        return date.Month is >= 6 and <= 9 && rate.SummerPricePerKwh is { } summer ? summer : rate.PricePerKwh;
    }

    public static double? Saved(IReadOnlyList<ElectricityRate> rates, DaySummary day) =>
        day.SolarKwh is { } kwh && PriceOn(rates, day.Date) is { } price ? Math.Round(kwh * price, 2) : null;

    /// <summary>Adds what each day saved (null where there's no solar data or no rate).</summary>
    public async Task<IReadOnlyList<DaySummary>> WithSavingsAsync(IReadOnlyList<DaySummary> list, CancellationToken ct = default)
    {
        var rates = (await GetSettingsAsync(ct)).RateList;
        return rates.Count == 0 ? list : list.Select(d => d with { Saved = Saved(rates, d) }).ToList();
    }

    public async Task<SavingsSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var rates = settings.RateList;
        var location = await site.GetLocationAsync(ct);
        var zone = location is null ? TimeZoneInfo.Local : SiteSettingsStore.Zone(location);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);

        var all = (await days.GetRangeAsync(DateOnly.MinValue, today, ct)).Where(d => d.SolarKwh is not null).ToList();
        var since = all.Count > 0 ? all[0].Date : (DateOnly?)null;
        var lifetimeKwh = Math.Round(all.Sum(d => d.SolarKwh ?? 0), 1);

        var months = all
            .GroupBy(d => (d.Date.Year, d.Date.Month))
            .Select(g => new MonthSavings(g.Key.Year, g.Key.Month, Math.Round(g.Sum(d => d.SolarKwh ?? 0), 1),
                Math.Round(g.Sum(d => Saved(rates, d) ?? 0), 2)))
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();

        if (rates.Count == 0)
            return new SavingsSummary(false, settings.Currency, null, null, null, null, lifetimeKwh, since, settings.SystemCost, null, null, months);

        double Sum(Func<DaySummary, bool> include) => Math.Round(all.Where(include).Sum(d => Saved(rates, d) ?? 0), 2);
        var lifetime = Sum(_ => true);
        double? payback = settings.SystemCost is { } cost and > 0 ? Math.Round(lifetime / cost * 100, 1) : null;
        return new SavingsSummary(true, settings.Currency,
            Today: Sum(d => d.Date == today),
            ThisMonth: Sum(d => d.Date.Year == today.Year && d.Date.Month == today.Month),
            ThisYear: Sum(d => d.Date.Year == today.Year),
            Lifetime: lifetime,
            LifetimeSolarKwh: lifetimeKwh,
            Since: since,
            SystemCost: settings.SystemCost,
            PaybackPercent: payback,
            CurrentPricePerKwh: PriceOn(rates, today),
            Months: months);
    }
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;

namespace SolPnl.Core.Site;

/// <summary>Reads and updates the per-day summaries (day_summary).</summary>
public sealed class DaySummaryStore(IDbContextFactory<SolPnlDb> dbFactory, TimeProvider time)
{
    public static string Key(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<DaySummary?> GetAsync(DateOnly date, CancellationToken ct = default)
    {
        var key = Key(date);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.DaySummaries.AsNoTracking().SingleOrDefaultAsync(d => d.Day == key, ct);
        return row is null ? null : ToDto(row);
    }

    /// <summary>Summaries in [from, to], oldest first. The "yyyy-MM-dd" keys sort chronologically as strings.</summary>
    public async Task<IReadOnlyList<DaySummary>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var fromKey = Key(from);
        var toKey = Key(to);
        // One row per day (~365 a year): filter in memory rather than rely on string comparison translating
        // identically on every database provider.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.DaySummaries.AsNoTracking().ToListAsync(ct);
        return rows
            .Where(r => string.CompareOrdinal(r.Day, fromKey) >= 0 && string.CompareOrdinal(r.Day, toKey) <= 0)
            .OrderBy(r => r.Day, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
    }

    /// <summary>Creates the day's row if needed and applies <paramref name="update"/> to it.</summary>
    public async Task UpdateAsync(DateOnly date, Action<DaySummaryEntity> update, CancellationToken ct = default)
    {
        var key = Key(date);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.DaySummaries.SingleOrDefaultAsync(d => d.Day == key, ct);
        if (row is null)
        {
            row = new DaySummaryEntity { Day = key };
            db.DaySummaries.Add(row);
        }
        update(row);
        row.UpdatedMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
    }

    public static DaySummary ToDto(DaySummaryEntity e) => new(
        DateOnly.ParseExact(e.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        e.SunriseMs is { } r ? DateTimeOffset.FromUnixTimeMilliseconds(r) : null,
        e.SunsetMs is { } s ? DateTimeOffset.FromUnixTimeMilliseconds(s) : null,
        e.ForecastHigh, e.ForecastLow, e.ForecastIrradiation, e.ClearSkyIrradiation,
        e.ActualHigh, e.ActualLow,
        e.SolarWh is { } sw ? Math.Round(sw / 1000, 2) : null,
        e.LoadWh is { } lw ? Math.Round(lw / 1000, 2) : null,
        e.GridWh is { } gw ? Math.Round(gw / 1000, 2) : null);
}

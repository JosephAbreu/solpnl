using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolPnl.Core.Data;

namespace SolPnl.Core.Storage;

public sealed record MaintenanceResult(int MinuteRollups, int HourRollups, int RawDeleted, int MinuteDeleted, int HourDeleted);

/// <summary>
/// Builds per-minute summaries from raw readings and per-hour summaries from minutes, then applies retention.
///
/// Each resolution has a watermark (rollup_state): every bucket starting before it is summarised. A batch's
/// summary rows and its new watermark are saved in one transaction, so a crash never double-counts or skips.
/// Readings that arrive for a bucket after it was summarised are not counted (they are still kept raw).
/// </summary>
public sealed class RollupMaintenance(
    IDbContextFactory<SolPnlDb> dbFactory,
    IOptions<RetentionOptions> options,
    TimeProvider time,
    ILogger<RollupMaintenance> logger)
{
    /// <summary>A minute is summarised only after this much time past its end, to allow for late polls.</summary>
    public static readonly TimeSpan RawGrace = TimeSpan.FromSeconds(30);

    private const long MaxMinuteBatchMs = 6 * Resolutions.HourMs; // bounds memory per batch
    private const long MaxHourBatchMs = 7 * 24 * Resolutions.HourMs;
    private const int MaxBatchesPerRun = 50;
    private const long DayMs = 24 * Resolutions.HourMs;

    public async Task<MaintenanceResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var minutes = await RollUpAsync(Resolutions.MinuteMs, cancellationToken);
        var hours = await RollUpAsync(Resolutions.HourMs, cancellationToken);
        var (raw, minuteDeleted, hourDeleted) = await PurgeAsync(cancellationToken);

        var result = new MaintenanceResult(minutes, hours, raw, minuteDeleted, hourDeleted);
        if (result != new MaintenanceResult(0, 0, 0, 0, 0))
            logger.LogDebug("Maintenance: {Result}", result);
        return result;
    }

    private async Task<int> RollUpAsync(long resolution, CancellationToken ct)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var state = await db.RollupStates.SingleOrDefaultAsync(s => s.ResolutionMs == resolution, ct);

            var start = state?.WatermarkMs ?? await FirstSourceTimestampAsync(db, resolution, ct);
            if (start is null) return total; // no source data yet

            var from = Floor(start.Value, resolution);
            var cutoff = await CutoffAsync(db, resolution, ct);
            var to = Math.Min(cutoff, from + (resolution == Resolutions.MinuteMs ? MaxMinuteBatchMs : MaxHourBatchMs));
            if (to <= from) return total; // caught up

            var rows = resolution == Resolutions.MinuteMs
                ? await AggregateRawAsync(db, from, to, ct)
                : await AggregateMinutesAsync(db, from, to, ct);

            db.Rollups.AddRange(rows);
            if (state is null)
                db.RollupStates.Add(new RollupStateEntity { ResolutionMs = resolution, WatermarkMs = to });
            else
                state.WatermarkMs = to;

            await db.SaveChangesAsync(ct);
            total += rows.Count;

            if (to == cutoff) return total;
        }
        return total;
    }

    private static async Task<long?> FirstSourceTimestampAsync(SolPnlDb db, long resolution, CancellationToken ct) =>
        resolution == Resolutions.MinuteMs
            ? await db.Readings.MinAsync(r => (long?)r.TimestampMs, ct)
            : await db.Rollups.Where(r => r.ResolutionMs == Resolutions.MinuteMs).MinAsync(r => (long?)r.BucketMs, ct);

    /// <summary>End (exclusive) of the last bucket that is complete and safe to summarise.</summary>
    private async Task<long> CutoffAsync(SolPnlDb db, long resolution, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (resolution == Resolutions.MinuteMs)
            return Floor((now - RawGrace).ToUnixTimeMilliseconds(), Resolutions.MinuteMs);

        // Hours are built from minutes, so they can't go past the minute watermark.
        var minuteWatermark = await db.RollupStates
            .Where(s => s.ResolutionMs == Resolutions.MinuteMs)
            .Select(s => (long?)s.WatermarkMs)
            .SingleOrDefaultAsync(ct) ?? 0;
        return Floor(Math.Min(now.ToUnixTimeMilliseconds(), minuteWatermark), Resolutions.HourMs);
    }

    private static async Task<List<RollupEntity>> AggregateRawAsync(SolPnlDb db, long from, long to, CancellationToken ct)
    {
        var groups = await db.Readings
            .Where(r => r.TimestampMs >= from && r.TimestampMs < to)
            .GroupBy(r => new { r.DeviceId, r.MetricId, Bucket = r.TimestampMs - r.TimestampMs % Resolutions.MinuteMs })
            .Select(g => new
            {
                g.Key.DeviceId,
                g.Key.MetricId,
                g.Key.Bucket,
                Sum = g.Sum(x => x.Value),
                Min = g.Min(x => x.Value),
                Max = g.Max(x => x.Value),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        return groups.Select(g => new RollupEntity
        {
            DeviceId = g.DeviceId,
            MetricId = g.MetricId,
            ResolutionMs = Resolutions.MinuteMs,
            BucketMs = g.Bucket,
            ValueSum = g.Sum,
            ValueMin = g.Min,
            ValueMax = g.Max,
            SampleCount = g.Count,
        }).ToList();
    }

    private static async Task<List<RollupEntity>> AggregateMinutesAsync(SolPnlDb db, long from, long to, CancellationToken ct)
    {
        var groups = await db.Rollups
            .Where(r => r.ResolutionMs == Resolutions.MinuteMs && r.BucketMs >= from && r.BucketMs < to)
            .GroupBy(r => new { r.DeviceId, r.MetricId, Bucket = r.BucketMs - r.BucketMs % Resolutions.HourMs })
            .Select(g => new
            {
                g.Key.DeviceId,
                g.Key.MetricId,
                g.Key.Bucket,
                Sum = g.Sum(x => x.ValueSum),
                Min = g.Min(x => x.ValueMin),
                Max = g.Max(x => x.ValueMax),
                Count = g.Sum(x => x.SampleCount),
            })
            .ToListAsync(ct);

        return groups.Select(g => new RollupEntity
        {
            DeviceId = g.DeviceId,
            MetricId = g.MetricId,
            ResolutionMs = Resolutions.HourMs,
            BucketMs = g.Bucket,
            ValueSum = g.Sum,
            ValueMin = g.Min,
            ValueMax = g.Max,
            SampleCount = g.Count,
        }).ToList();
    }

    private async Task<(int Raw, int Minute, int Hour)> PurgeAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var watermarks = await db.RollupStates.AsNoTracking().ToDictionaryAsync(s => s.ResolutionMs, s => s.WatermarkMs, ct);

        // Plain, parameterised SQL rather than EF's ExecuteDeleteAsync: the MySQL provider translates that to
        // "DELETE FROM t AS alias ...", which MySQL accepts but MariaDB rejects. These statements are identical
        // on every supported database.
        var raw = 0;
        if (o.RawDays > 0 && watermarks.TryGetValue(Resolutions.MinuteMs, out var minuteWatermark))
        {
            var cut = Math.Min(now - o.RawDays * DayMs, minuteWatermark);
            raw = await db.Database.ExecuteSqlAsync($"delete from reading where ts_ms < {cut}", ct);
        }

        var minute = 0;
        if (o.MinuteDays > 0 && watermarks.TryGetValue(Resolutions.HourMs, out var hourWatermark))
        {
            var cut = Math.Min(now - o.MinuteDays * DayMs, hourWatermark);
            minute = await db.Database.ExecuteSqlAsync(
                $"delete from reading_rollup where resolution_ms = {Resolutions.MinuteMs} and bucket_ms < {cut}", ct);
        }

        var hour = 0;
        if (o.HourDays > 0)
        {
            var cut = now - o.HourDays * DayMs;
            hour = await db.Database.ExecuteSqlAsync(
                $"delete from reading_rollup where resolution_ms = {Resolutions.HourMs} and bucket_ms < {cut}", ct);
        }

        if (raw + minute + hour > 0)
            logger.LogInformation("Retention removed {Raw} raw readings, {Minute} minute and {Hour} hour summaries.", raw, minute, hour);
        return (raw, minute, hour);
    }

    internal static long Floor(long ms, long resolution) => ms - ((ms % resolution) + resolution) % resolution;
}

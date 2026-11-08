using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Grid;

/// <summary>A grid event that opened or closed for a device during a check.</summary>
public sealed record GridChange(string DeviceKey, TrackedGridEvent Event)
{
    public bool Opened => Event.EndedAt is null;
}

/// <summary>
/// Watches grid voltage from every device that reports it and records outages and low/high voltage in grid_event.
/// <see cref="Notifications.NotificationService"/> calls <see cref="CheckAsync"/> every minute and turns the changes
/// into alerts. Events are recorded whether or not grid notifications are turned on.
/// </summary>
public sealed class GridMonitor(IDbContextFactory<SolPnlDb> dbFactory, IReadingStore store, TimeProvider time)
{
    /// <summary>How far back to look the first time a device is checked (e.g. after a restart).</summary>
    private static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, (GridTracker Tracker, DateTimeOffset Watermark)> _devices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Grid voltage metrics, per leg or single.</summary>
    public static bool IsGridVoltage(string metric) =>
        metric == Metrics.GridVoltage || metric.StartsWith(Metrics.GridVoltage + ".l", StringComparison.Ordinal);

    /// <summary>True while an outage is open for the device (its "offline" alert is then the outage's doing).</summary>
    public bool HasOutage(string deviceKey) => _devices.TryGetValue(deviceKey, out var d) && d.Tracker.Outage is not null;

    public async Task<IReadOnlyList<GridChange>> CheckAsync(IEnumerable<string> deviceKeys, double? low, double? high, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var changes = new List<GridChange>();
            var now = time.GetUtcNow();
            foreach (var deviceKey in deviceKeys)
            {
                var metrics = (await store.GetLatestAsync(deviceKey, ct)).Select(r => r.Metric).Where(IsGridVoltage).Distinct().ToList();
                if (metrics.Count == 0) continue;

                if (!_devices.TryGetValue(deviceKey, out var state))
                    state = await LoadAsync(deviceKey, now, ct);

                var samples = await SamplesAsync(deviceKey, metrics, state.Watermark, now, ct);
                var changed = state.Tracker.Process(samples, now, low, high);
                if (samples.Count > 0) state = state with { Watermark = samples[^1].At };
                _devices[deviceKey] = state;

                await SaveAsync(deviceKey, state.Tracker, changed, ct);
                changes.AddRange(changed.Select(e => new GridChange(deviceKey, e)));
            }
            return changes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Grid events from the last <paramref name="days"/> days, newest first.</summary>
    public async Task<GridHistory> GetHistoryAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var now = time.GetUtcNow();
        var since = (now - TimeSpan.FromDays(days)).ToUnixTimeMilliseconds();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GridEvents.AsNoTracking()
            .Where(e => e.StartedMs >= since || e.EndedMs == null || e.EndedMs >= since)
            .OrderByDescending(e => e.StartedMs)
            .Take(500)
            .ToListAsync(ct);
        var events = rows.Select(ToDto).ToList();
        var outages = events.Where(e => e.Kind == GridEventKinds.Outage).ToList();
        var minutes = outages.Sum(e => ((e.EndedAt ?? now) - Max(e.StartedAt, now - TimeSpan.FromDays(days))).TotalMinutes);
        return new GridHistory(events, outages.Count, Math.Round(minutes, 1), days);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    public static GridEvent ToDto(GridEventEntity e) => new(e.Id, e.DeviceKey, e.Kind,
        DateTimeOffset.FromUnixTimeMilliseconds(e.StartedMs),
        e.EndedMs is { } end ? DateTimeOffset.FromUnixTimeMilliseconds(end) : null, e.ExtremeValue);

    /// <summary>
    /// A new tracker for the device, with its open events resumed. Starts after the device's newest recorded event so
    /// samples that already produced an event aren't processed twice.
    /// </summary>
    private async Task<(GridTracker, DateTimeOffset)> LoadAsync(string deviceKey, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recent = await db.GridEvents.AsNoTracking().Where(e => e.DeviceKey == deviceKey)
            .OrderByDescending(e => e.StartedMs).Take(10).ToListAsync(ct);

        var tracker = new GridTracker();
        foreach (var open in recent.Where(e => e.EndedMs is null))
        {
            tracker.Resume(new TrackedGridEvent
            {
                Id = open.Id, Kind = open.Kind, StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(open.StartedMs),
                Extreme = open.ExtremeValue ?? 0,
            });
        }

        var watermark = now - FirstLook;
        foreach (var e in recent)
        {
            var latest = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(e.StartedMs, e.EndedMs ?? 0));
            if (latest > watermark) watermark = latest;
        }
        return (tracker, watermark);
    }

    /// <summary>Raw samples after <paramref name="after"/>, one per poll, with every leg's voltage.</summary>
    private async Task<List<GridSample>> SamplesAsync(string deviceKey, IReadOnlyList<string> metrics, DateTimeOffset after,
        DateTimeOffset now, CancellationToken ct)
    {
        var byTime = new SortedDictionary<DateTimeOffset, List<double>>();
        foreach (var metric in metrics)
        {
            var history = await store.GetHistoryAsync(deviceKey, metric, after, now, HistoryResolution.Raw, ct);
            foreach (var point in history.Points.Where(p => p.Timestamp > after))
            {
                if (!byTime.TryGetValue(point.Timestamp, out var legs)) byTime[point.Timestamp] = legs = [];
                legs.Add(point.Value);
            }
        }
        return byTime.Select(kv => new GridSample(kv.Key, kv.Value)).ToList();
    }

    private async Task SaveAsync(string deviceKey, GridTracker tracker, List<TrackedGridEvent> changed, CancellationToken ct)
    {
        var touched = changed.Concat(new[] { tracker.Outage, tracker.Voltage }.OfType<TrackedGridEvent>()).Distinct().ToList();
        if (touched.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = touched.Where(e => e.Id != 0).Select(e => e.Id).ToList();
        var existing = await db.GridEvents.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var added = new List<(TrackedGridEvent Event, GridEventEntity Row)>();
        foreach (var e in touched)
        {
            if (e.Id != 0 && existing.TryGetValue(e.Id, out var row))
            {
                row.EndedMs = e.EndedAt?.ToUnixTimeMilliseconds();
                row.ExtremeValue = Math.Round(e.Extreme, 1);
                continue;
            }
            row = new GridEventEntity
            {
                DeviceKey = deviceKey, Kind = e.Kind, StartedMs = e.StartedAt.ToUnixTimeMilliseconds(),
                EndedMs = e.EndedAt?.ToUnixTimeMilliseconds(), ExtremeValue = Math.Round(e.Extreme, 1),
            };
            db.GridEvents.Add(row);
            added.Add((e, row));
        }
        await db.SaveChangesAsync(ct);
        foreach (var (e, row) in added) e.Id = row.Id;
    }
}

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;

namespace SolPnl.Core.Storage;

/// <summary>
/// EF Core implementation of <see cref="IReadingStore"/>. Uses only portable LINQ (no provider-specific SQL),
/// so it behaves the same on SQLite, MySQL/MariaDB and SQL Server.
/// </summary>
public sealed class EfReadingStore(
    IDbContextFactory<SolPnlDb> dbFactory,
    IOptions<RetentionOptions> retention,
    TimeProvider time) : IReadingStore
{
    // Device and metric ids never change once created, so cache them to avoid lookups on every poll.
    private readonly ConcurrentDictionary<string, int> _deviceIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _metricIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task SaveAsync(DeviceInfo device, IReadOnlyList<Reading> readings, CancellationToken cancellationToken = default)
    {
        if (readings.Count == 0) return;

        // Single writer: keeps device/metric creation race-free without provider-specific upserts.
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var deviceId = await GetOrCreateDeviceAsync(db, device, cancellationToken);

            var rows = new Dictionary<int, ReadingEntity>();
            foreach (var reading in readings)
            {
                var metricId = await GetOrCreateMetricAsync(db, reading.Metric, reading.Unit, cancellationToken);
                // Last one wins if a plugin reports the same metric twice in one poll.
                rows[metricId] = new ReadingEntity
                {
                    DeviceId = deviceId,
                    MetricId = metricId,
                    TimestampMs = reading.Timestamp.ToUnixTimeMilliseconds(),
                    Value = reading.Value,
                };
            }

            db.Readings.AddRange(rows.Values);

            var latest = await db.LatestReadings
                .Where(l => l.DeviceId == deviceId)
                .ToDictionaryAsync(l => l.MetricId, cancellationToken);

            foreach (var row in rows.Values)
            {
                if (latest.TryGetValue(row.MetricId, out var existing))
                {
                    if (row.TimestampMs < existing.TimestampMs) continue;
                    existing.TimestampMs = row.TimestampMs;
                    existing.Value = row.Value;
                }
                else
                {
                    db.LatestReadings.Add(new LatestReadingEntity
                    {
                        DeviceId = deviceId,
                        MetricId = row.MetricId,
                        TimestampMs = row.TimestampMs,
                        Value = row.Value,
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.DeviceKey).ToListAsync(cancellationToken);
        return devices.Select(ToDeviceInfo).ToList();
    }

    public async Task<IReadOnlyList<Reading>> GetLatestAsync(string? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query =
            from l in db.LatestReadings.AsNoTracking()
            join d in db.Devices on l.DeviceId equals d.Id
            join m in db.Metrics on l.MetricId equals m.Id
            select new { d.DeviceKey, m.Name, m.Unit, l.TimestampMs, l.Value };

        if (deviceId is not null)
            query = query.Where(x => x.DeviceKey == deviceId);

        var rows = await query.ToListAsync(cancellationToken);
        return rows
            .OrderBy(x => x.DeviceKey, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new Reading(x.DeviceKey, x.Name, x.Value, x.Unit, DateTimeOffset.FromUnixTimeMilliseconds(x.TimestampMs)))
            .ToList();
    }

    /// <summary>Longest range served at raw resolution (~720 points at a 10 s poll interval).</summary>
    public static readonly TimeSpan MaxRawSpan = TimeSpan.FromHours(2);

    /// <summary>Longest range served at minute resolution (~4,300 points).</summary>
    public static readonly TimeSpan MaxMinuteSpan = TimeSpan.FromDays(3);

    public static HistoryResolution ChooseResolution(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, RetentionOptions retention)
    {
        var span = to - from;
        bool Retained(int days) => days <= 0 || from >= now.AddDays(-days);

        if (span <= MaxRawSpan && Retained(retention.RawDays)) return HistoryResolution.Raw;
        if (span <= MaxMinuteSpan && Retained(retention.MinuteDays)) return HistoryResolution.Minute;
        return HistoryResolution.Hour;
    }

    public async Task<HistoryResult> GetHistoryAsync(
        string deviceId, string metric, DateTimeOffset from, DateTimeOffset to,
        HistoryResolution? resolution = null, CancellationToken cancellationToken = default)
    {
        var chosen = resolution ?? ChooseResolution(from, to, time.GetUtcNow(), retention.Value);
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.AsNoTracking().Where(d => d.DeviceKey == deviceId).Select(d => (int?)d.Id)
            .SingleOrDefaultAsync(cancellationToken);
        var metricRow = await db.Metrics.AsNoTracking().Where(m => m.Name == metric).Select(m => new { m.Id, m.Unit })
            .SingleOrDefaultAsync(cancellationToken);

        if (device is null || metricRow is null)
            return new HistoryResult(deviceId, metric, metricRow?.Unit, chosen, []);

        var deviceRowId = device.Value;
        List<HistoryPoint> points;
        if (chosen == HistoryResolution.Raw)
        {
            var rows = await db.Readings.AsNoTracking()
                .Where(r => r.DeviceId == deviceRowId && r.MetricId == metricRow.Id && r.TimestampMs >= fromMs && r.TimestampMs <= toMs)
                .OrderBy(r => r.TimestampMs)
                .Select(r => new { r.TimestampMs, r.Value })
                .ToListAsync(cancellationToken);
            points = rows.Select(r => new HistoryPoint(DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampMs), r.Value)).ToList();
        }
        else
        {
            var bucketMs = chosen == HistoryResolution.Minute ? Resolutions.MinuteMs : Resolutions.HourMs;
            var firstBucket = RollupMaintenance.Floor(fromMs, bucketMs); // include the bucket containing 'from'
            var rows = await db.Rollups.AsNoTracking()
                .Where(r => r.DeviceId == deviceRowId && r.MetricId == metricRow.Id && r.ResolutionMs == bucketMs
                            && r.BucketMs >= firstBucket && r.BucketMs <= toMs)
                .OrderBy(r => r.BucketMs)
                .Select(r => new { r.BucketMs, r.ValueSum, r.ValueMin, r.ValueMax, r.SampleCount })
                .ToListAsync(cancellationToken);
            points = rows.Select(r => new HistoryPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(r.BucketMs),
                Math.Round(r.ValueSum / r.SampleCount, 3),
                r.ValueMin,
                r.ValueMax)).ToList();
        }

        return new HistoryResult(deviceId, metric, metricRow.Unit, chosen, points);
    }

    private async Task<int> GetOrCreateDeviceAsync(SolPnlDb db, DeviceInfo device, CancellationToken ct)
    {
        var entity = await db.Devices.SingleOrDefaultAsync(d => d.DeviceKey == device.DeviceId, ct);
        if (entity is null)
        {
            entity = new DeviceEntity
            {
                DeviceKey = device.DeviceId,
                Vendor = device.Vendor,
                Model = device.Model,
                SerialNumber = device.SerialNumber,
                FirmwareVersion = device.FirmwareVersion,
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            db.Devices.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        else if (!_deviceIds.ContainsKey(device.DeviceId))
        {
            // First save since start: refresh details that may have changed (e.g. firmware update).
            entity.Vendor = device.Vendor;
            entity.Model = device.Model;
            entity.SerialNumber = device.SerialNumber;
            entity.FirmwareVersion = device.FirmwareVersion;
        }

        _deviceIds[device.DeviceId] = entity.Id;
        return entity.Id;
    }

    private async Task<int> GetOrCreateMetricAsync(SolPnlDb db, string name, string unit, CancellationToken ct)
    {
        if (_metricIds.TryGetValue(name, out var cached)) return cached;

        var entity = await db.Metrics.SingleOrDefaultAsync(m => m.Name == name, ct);
        if (entity is null)
        {
            entity = new MetricEntity { Name = name, Unit = unit };
            db.Metrics.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        _metricIds[name] = entity.Id;
        return entity.Id;
    }

    private static DeviceInfo ToDeviceInfo(DeviceEntity d) =>
        new(d.DeviceKey, d.Vendor, d.Model, d.SerialNumber, d.FirmwareVersion);
}

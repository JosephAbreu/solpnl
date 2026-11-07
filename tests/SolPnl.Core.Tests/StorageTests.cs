using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;
using SolPnl.Core.Storage;
using Testcontainers.MariaDb;
using Testcontainers.MsSql;
using Testcontainers.MySql;

namespace SolPnl.Core.Tests;

/// <summary>
/// The same storage tests run against every supported driver. Container-backed drivers need Docker and are
/// tagged Category=Containers; run only the fast ones with: dotnet test --filter "Category!=Containers"
/// </summary>
public abstract class StorageTests : IAsyncLifetime
{
    private static readonly DeviceInfo Inverter = new("solarman-1", "SRNE-compatible", "Split-phase", "1", null);
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero); // hour-aligned

    protected abstract Task<string> StartAsync();
    protected abstract string DriverName { get; }
    public virtual Task DisposeAsync() => Task.CompletedTask;

    private DbSettings _settings = null!;
    private IDbContextFactory<SolPnlDb> _factory = null!;
    private readonly ManualTimeProvider _time = new(T0.AddMinutes(1));
    private readonly RetentionOptions _retention = new();

    public async Task InitializeAsync()
    {
        var connectionString = await StartAsync();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Db:DriverName"] = DriverName,
                ["Db:ConnectionString"] = connectionString,
            })
            .Build();
        _settings = DbSettings.FromConfiguration(config);
        _factory = new Factory(_settings);
    }

    private MigrationService Migrations() => new(_factory, _settings, NullLogger<MigrationService>.Instance);
    private EfReadingStore Store() => new(_factory, Options.Create(_retention), _time);
    private RollupMaintenance Maintenance() =>
        new(_factory, Options.Create(_retention), _time, NullLogger<RollupMaintenance>.Instance);

    [Fact]
    public async Task Migrate_AppliesAllScripts_AndIsIdempotent()
    {
        var migrations = Migrations();
        Assert.True(migrations.GetScripts().Count >= 2);

        await migrations.MigrateAsync();
        await migrations.MigrateAsync(); // second run must be a no-op

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.Devices.CountAsync());
        Assert.Equal(0, await db.Rollups.CountAsync());
    }

    [Fact]
    public async Task SaveAndQuery_RoundTrips()
    {
        await Migrations().MigrateAsync();
        var store = Store();

        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 1000, "W", T0), R(Metrics.LoadPower, 600, "W", T0)]);
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 1200.5, "W", T0.AddSeconds(10)), R(Metrics.LoadPower, 650, "W", T0.AddSeconds(10))]);

        // A fresh store (empty caches) must find existing devices and metrics instead of duplicating them.
        var store2 = Store();
        await store2.SaveAsync(Inverter, [R(Metrics.PvPower, 900, "W", T0.AddSeconds(20))]);

        var devices = await store2.GetDevicesAsync();
        Assert.Equal(Inverter, Assert.Single(devices));

        var latest = (await store2.GetLatestAsync()).ToDictionary(r => r.Metric);
        Assert.Equal(2, latest.Count);
        Assert.Equal(900, latest[Metrics.PvPower].Value);
        Assert.Equal(T0.AddSeconds(20), latest[Metrics.PvPower].Timestamp);
        Assert.Equal(650, latest[Metrics.LoadPower].Value);
        Assert.Equal("W", latest[Metrics.LoadPower].Unit);

        Assert.Empty(await store2.GetLatestAsync("no-such-device"));

        var history = await store2.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0, T0.AddSeconds(20));
        Assert.Equal(HistoryResolution.Raw, history.Resolution);
        Assert.Equal("W", history.Unit);
        Assert.Equal([1000, 1200.5, 900], history.Points.Select(p => p.Value));
        Assert.Equal([T0, T0.AddSeconds(10), T0.AddSeconds(20)], history.Points.Select(p => p.Timestamp));

        var window = await store2.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0.AddSeconds(5), T0.AddSeconds(15));
        Assert.Equal(1200.5, Assert.Single(window.Points).Value);

        var missing = await store2.GetHistoryAsync("no-such-device", Metrics.PvPower, T0, T0.AddSeconds(20));
        Assert.Empty(missing.Points);
    }

    [Fact]
    public async Task Save_OlderReading_DoesNotOverwriteLatest()
    {
        await Migrations().MigrateAsync();
        var store = Store();

        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 500, "W", T0.AddSeconds(10))]);
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 400, "W", T0)]);

        Assert.Equal(500, Assert.Single(await store.GetLatestAsync()).Value);
        Assert.Equal(2, (await store.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0, T0.AddMinutes(1))).Points.Count);
    }

    [Fact]
    public async Task Rollups_SummariseMinutesAndHours_AndRetentionPurgesOnlySummarisedData()
    {
        await Migrations().MigrateAsync();
        var store = Store();

        // Two hours of 10-second readings with value = sample index k (k = 0..720), like a real poll stream.
        const int samples = 721;
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 0, "W", T0)]);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var deviceId = await db.Devices.Select(d => d.Id).SingleAsync();
            var metricId = await db.Metrics.Select(m => m.Id).SingleAsync();
            db.Readings.AddRange(Enumerable.Range(1, samples - 1).Select(k => new ReadingEntity
            {
                DeviceId = deviceId,
                MetricId = metricId,
                TimestampMs = T0.AddSeconds(10 * k).ToUnixTimeMilliseconds(),
                Value = k,
            }));
            await db.SaveChangesAsync();
        }

        _time.Now = T0.AddHours(2).AddMinutes(5);
        var first = await Maintenance().RunOnceAsync();
        Assert.Equal(121, first.MinuteRollups); // buckets T0 .. T0+2h (the last holds only k = 720)
        Assert.Equal(2, first.HourRollups);     // hours T0 and T0+1h; T0+2h isn't complete yet
        Assert.Equal(0, first.RawDeleted);      // raw retention is 7 days

        // Running again with nothing new must not add or duplicate anything.
        var second = await Maintenance().RunOnceAsync();
        Assert.Equal(new MaintenanceResult(0, 0, 0, 0, 0), second);

        var minutes = await store.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0, T0.AddHours(2), HistoryResolution.Minute);
        Assert.Equal(121, minutes.Points.Count);
        Assert.Equal(new HistoryPoint(T0, 2.5, 0, 5), minutes.Points[0]);                 // k = 0..5
        Assert.Equal(new HistoryPoint(T0.AddMinutes(1), 8.5, 6, 11), minutes.Points[1]); // k = 6..11
        Assert.Equal(new HistoryPoint(T0.AddHours(2), 720, 720, 720), minutes.Points[^1]);

        var hours = await store.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0, T0.AddHours(2), HistoryResolution.Hour);
        Assert.Equal([new HistoryPoint(T0, 179.5, 0, 359), new HistoryPoint(T0.AddHours(1), 539.5, 360, 719)], hours.Points);

        // Eight days later: raw readings are past the 7-day limit and summarised, so they go; summaries stay.
        _time.Now = T0.AddDays(8);
        var later = await Maintenance().RunOnceAsync();
        Assert.Equal(samples, later.RawDeleted);
        Assert.Equal(0, later.MinuteDeleted);

        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(0, await db.Readings.CountAsync());

        // An old range now falls back to summaries automatically.
        var old = await store.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0, T0.AddHours(1));
        Assert.Equal(HistoryResolution.Minute, old.Resolution);
        Assert.Equal(61, old.Points.Count);
        // Moving the clock forward also completed hour T0+2h, which holds the final reading only.
        var allHours = await store.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0, T0.AddHours(2), HistoryResolution.Hour);
        Assert.Equal(3, allHours.Points.Count);
        Assert.Equal(new HistoryPoint(T0.AddHours(2), 720, 720, 720), allHours.Points[^1]);
    }

    [Fact]
    public async Task Retention_KeepsRawDataThatHasNotBeenSummarised()
    {
        await Migrations().MigrateAsync();
        var store = Store();
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 1, "W", T0)]);

        // Even with the shortest raw retention, a reading in a minute that isn't complete yet can't be summarised,
        // so it must not be deleted.
        _retention.RawDays = 1;
        _time.Now = T0.AddSeconds(5);
        var result = await Maintenance().RunOnceAsync();
        Assert.Equal(new MaintenanceResult(0, 0, 0, 0, 0), result);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Readings.CountAsync());
    }

    private static Reading R(string metric, double value, string unit, DateTimeOffset ts) =>
        new(Inverter.DeviceId, metric, value, unit, ts);

    private sealed class Factory(DbSettings settings) : IDbContextFactory<SolPnlDb>
    {
        public SolPnlDb CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<SolPnlDb>();
            settings.Configure(builder);
            return new SolPnlDb(builder.Options);
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class SqliteStorageTests : StorageTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"solpnl-test-{Guid.NewGuid():N}.db");
    protected override string DriverName => "sqlite";
    protected override Task<string> StartAsync() => Task.FromResult($"Data Source={_path};Pooling=false");

    public override Task DisposeAsync()
    {
        File.Delete(_path);
        return Task.CompletedTask;
    }
}

[Trait("Category", "Containers")]
public sealed class MySqlStorageTests : StorageTests
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.4").Build();
    protected override string DriverName => "mysql";
    protected override async Task<string> StartAsync() { await _container.StartAsync(); return _container.GetConnectionString(); }
    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[Trait("Category", "Containers")]
public sealed class MariaDbStorageTests : StorageTests
{
    private readonly MariaDbContainer _container = new MariaDbBuilder("mariadb:11.4").Build();
    protected override string DriverName => "mariadb";
    protected override async Task<string> StartAsync() { await _container.StartAsync(); return _container.GetConnectionString(); }
    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[Trait("Category", "Containers")]
public sealed class SqlServerStorageTests : StorageTests
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    protected override string DriverName => "mssql";
    protected override async Task<string> StartAsync() { await _container.StartAsync(); return _container.GetConnectionString(); }
    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

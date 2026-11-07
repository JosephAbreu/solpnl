using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Data;
using SolPnl.Core.Devices;
using SolPnl.Core.Setup;
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

    private DeviceRegistry Registry(params IDevicePluginType[] types) =>
        new(_factory, types, Store(), NullLogger<DeviceRegistry>.Instance);

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    [Fact]
    public async Task Registry_AddsRemovesDevices_AndPluginToggleStartsAndStopsPolling()
    {
        await Migrations().MigrateAsync();
        var fake = new FakeDevicePluginType();
        await using var registry = Registry(fake);
        await registry.InitializeAsync(EmptyConfig());
        Assert.Empty(registry.ActiveDevices);

        // Add: unknown keys dropped, secrets never returned, polling starts.
        var garage = await registry.AddAsync(new AddDeviceRequest(" Garage ", "fake",
            new() { ["id"] = "a", ["secret"] = "s3cret", ["unknown"] = "x" }));
        Assert.Equal("Garage", garage.Name);
        Assert.Equal("fake-a", garage.DeviceId);
        Assert.Null(garage.Settings["secret"]);
        Assert.False(garage.Settings.ContainsKey("unknown"));
        Assert.Equal("fake-a", Assert.Single(registry.ActiveDevices).DeviceKey);

        // Invalid and duplicate devices are rejected.
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AddAsync(new AddDeviceRequest("X", "fake", new())));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AddAsync(new AddDeviceRequest("", "fake", new() { ["id"] = "b" })));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AddAsync(new AddDeviceRequest("X", "nope", new())));
        var dup = await Assert.ThrowsAsync<DeviceConfigurationException>(() =>
            registry.AddAsync(new AddDeviceRequest("Again", "fake", new() { ["id"] = "a" })));
        Assert.Contains("Garage", dup.Message);

        // Disabling the plugin stops polling (instance disposed) but keeps the configuration.
        var running = (FakeDevice)registry.ActiveDevices[0].Plugin;
        var disabled = await registry.SetPluginEnabledAsync("fake", false);
        Assert.False(disabled.Enabled);
        Assert.Equal(1, disabled.DeviceCount);
        Assert.Empty(registry.ActiveDevices);
        Assert.True(running.Disposed);
        await Assert.ThrowsAsync<DeviceConfigurationException>(() =>
            registry.AddAsync(new AddDeviceRequest("Shed", "fake", new() { ["id"] = "b" })));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.SetPluginEnabledAsync("nope", true));

        // State survives a restart (a new registry over the same database).
        await using (var restarted = Registry(new FakeDevicePluginType()))
        {
            await restarted.InitializeAsync(EmptyConfig());
            Assert.Empty(restarted.ActiveDevices);
        }

        await registry.SetPluginEnabledAsync("fake", true);
        Assert.Single(registry.ActiveDevices);

        // Remove keeping history, then remove another device deleting its history.
        var store = Store();
        await store.SaveAsync(new DeviceInfo("fake-a", "Test", "Fake", null, null), [new Reading("fake-a", Metrics.PvPower, 1, "W", T0)]);
        Assert.True(await registry.RemoveAsync(garage.Id, deleteHistory: false));
        Assert.Empty(registry.ActiveDevices);
        Assert.Contains(await store.GetDevicesAsync(), d => d.DeviceId == "fake-a");

        var shed = await registry.AddAsync(new AddDeviceRequest("Shed", "fake", new() { ["id"] = "b" }));
        await store.SaveAsync(new DeviceInfo("fake-b", "Test", "Fake", null, null), [new Reading("fake-b", Metrics.PvPower, 1, "W", T0)]);
        Assert.True(await registry.RemoveAsync(shed.Id, deleteHistory: true));
        Assert.DoesNotContain(await store.GetDevicesAsync(), d => d.DeviceId == "fake-b");
        Assert.Empty(await store.GetLatestAsync("fake-b"));

        Assert.False(await registry.RemoveAsync(12345, deleteHistory: false));

        // A device whose history was kept can be added back and keeps recording under the same key.
        await registry.AddAsync(new AddDeviceRequest("Garage", "fake", new() { ["id"] = "a" }));
        await store.SaveAsync(new DeviceInfo("fake-a", "Test", "Fake", null, null), [new Reading("fake-a", Metrics.PvPower, 2, "W", T0.AddSeconds(10))]);
        Assert.Equal(2, Assert.Single(await store.GetLatestAsync("fake-a")).Value);
    }

    [Fact]
    public async Task Registry_ImportsLegacyPluginConfigOnlyOnce()
    {
        await Migrations().MigrateAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:SolarmanSrne:Host"] = "192.168.4.31",
            ["Plugins:SolarmanSrne:LoggerSerial"] = "3589272256",
        }).Build();

        await using (var registry = Registry(new SrneLikeType()))
        {
            await registry.InitializeAsync(config);
            var imported = Assert.Single(await registry.GetDevicesAsync());
            Assert.Equal("Inverter", imported.Name);
            Assert.Equal("solarman-3589272256", imported.DeviceId);
            Assert.Equal("192.168.4.31", imported.Settings["host"]);
            Assert.Single(registry.ActiveDevices);

            // The user removes it in the UI...
            Assert.True(await registry.RemoveAsync(imported.Id, deleteHistory: false));
        }

        // ...and it must not come back from the old config section on the next start.
        await using (var registry = Registry(new SrneLikeType()))
        {
            await registry.InitializeAsync(config);
            Assert.Empty(await registry.GetDevicesAsync());
        }
    }

    [Fact]
    public async Task Registry_TestReportsReachableAndUnreachableDevices()
    {
        await Migrations().MigrateAsync();
        await using var registry = Registry(new FakeDevicePluginType());

        var ok = await registry.TestAsync("fake", new Dictionary<string, string?> { ["id"] = "x" });
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("fake-x", ok.DeviceId);
        Assert.Equal(1, ok.ReadingCount);

        var unreachable = await registry.TestAsync("fake", new Dictionary<string, string?> { ["id"] = "x", ["fail"] = "true" });
        Assert.False(unreachable.Ok);
        Assert.Contains("unreachable", unreachable.Message);

        Assert.False((await registry.TestAsync("fake", new Dictionary<string, string?>())).Ok);
        Assert.False((await registry.TestAsync("nope", new Dictionary<string, string?> { ["id"] = "x" })).Ok);
        Assert.Empty(await registry.GetDevicesAsync()); // testing never saves
    }

    /// <summary>Stands in for the SRNE plugin type (same id and setting keys) without network access.</summary>
    private sealed class SrneLikeType : IDevicePluginType
    {
        public string Id => "solarman-srne";
        public string DisplayName => "SRNE-like";
        public string Description => "Test";
        public IReadOnlyList<SettingField> Settings { get; } =
            [new("host", "Host"), new("loggerSerial", "Serial", SettingKind.Number), new("port", "Port", Required: false), new("slaveId", "Slave", Required: false)];

        public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) =>
            new FakeDevice(Id, $"solarman-{settings["loggerSerial"]}", fail: false);
    }

    [Fact]
    public async Task SetupWizard_TestsCreatesSchemaAndSavesSettings()
    {
        var dir = Directory.CreateTempSubdirectory("solpnl-setup-");
        try
        {
            var file = new LocalSettingsFile(Path.Combine(dir.FullName, LocalSettingsFile.FileName));
            var request = new StorageSetupRequest { Code = "x", Driver = DriverName, ConnectionString = _settings.ConnectionString };
            var settings = StorageSetupService.BuildSettings(request);

            var test = await StorageSetupService.TestAsync(settings);
            Assert.True(test.Ok, test.Message);
            Assert.False(string.IsNullOrWhiteSpace(test.ServerVersion));

            var applied = await new StorageSetupService(file).ApplyAsync(settings);
            Assert.True(applied.Ok, applied.Message);

            // Schema exists and the saved settings round-trip to the same database.
            var saved = DbSettings.FromConfiguration(new ConfigurationBuilder().AddJsonFile(file.Path).Build());
            Assert.Equal(_settings.Driver, saved.Driver);
            await using var db = new SolPnlDbFactory(saved).CreateDbContext();
            Assert.Equal(0, await db.Devices.CountAsync());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

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

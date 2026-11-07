using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

    protected abstract Task<string> StartAsync();
    protected abstract string DriverName { get; }
    public virtual Task DisposeAsync() => Task.CompletedTask;

    private DbSettings _settings = null!;
    private IDbContextFactory<SolPnlDb> _factory = null!;

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

    [Fact]
    public async Task Migrate_AppliesAllScripts_AndIsIdempotent()
    {
        var migrations = Migrations();
        Assert.NotEmpty(migrations.GetScripts());

        await migrations.MigrateAsync();
        await migrations.MigrateAsync(); // second run must be a no-op

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.Devices.CountAsync());
    }

    [Fact]
    public async Task SaveAndQuery_RoundTrips()
    {
        await Migrations().MigrateAsync();
        var store = new EfReadingStore(_factory);

        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 1000, "W", T0), R(Metrics.LoadPower, 600, "W", T0)]);
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 1200.5, "W", T0.AddSeconds(10)), R(Metrics.LoadPower, 650, "W", T0.AddSeconds(10))]);

        // A fresh store (empty caches) must find existing devices and metrics instead of duplicating them.
        var store2 = new EfReadingStore(_factory);
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
        Assert.Equal([1000, 1200.5, 900], history.Select(p => p.Value));
        Assert.Equal([T0, T0.AddSeconds(10), T0.AddSeconds(20)], history.Select(p => p.Timestamp));

        var window = await store2.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0.AddSeconds(5), T0.AddSeconds(15));
        Assert.Equal(1200.5, Assert.Single(window).Value);
    }

    [Fact]
    public async Task Save_OlderReading_DoesNotOverwriteLatest()
    {
        await Migrations().MigrateAsync();
        var store = new EfReadingStore(_factory);

        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 500, "W", T0.AddSeconds(10))]);
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 400, "W", T0)]);

        Assert.Equal(500, Assert.Single(await store.GetLatestAsync()).Value);
        Assert.Equal(2, (await store.GetHistoryAsync(Inverter.DeviceId, Metrics.PvPower, T0, T0.AddMinutes(1))).Count);
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

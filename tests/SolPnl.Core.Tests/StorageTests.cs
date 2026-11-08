using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Backup;
using SolPnl.Core.Data;
using SolPnl.Core.Devices;
using SolPnl.Core.Grid;
using SolPnl.Core.Notifications;
using SolPnl.Core.Setup;
using SolPnl.Core.Site;
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
            ["Plugins:SolarmanSrne:Host"] = "192.168.1.50",
            ["Plugins:SolarmanSrne:LoggerSerial"] = "2712345678",
        }).Build();

        await using (var registry = Registry(new SrneLikeType()))
        {
            await registry.InitializeAsync(config);
            var imported = Assert.Single(await registry.GetDevicesAsync());
            Assert.Equal("Inverter", imported.Name);
            Assert.Equal("solarman-2712345678", imported.DeviceId);
            Assert.Equal("192.168.1.50", imported.Settings["host"]);
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

    [Fact]
    public async Task Site_LogsWeather_KeepsForecast_AndSummarisesTheDay()
    {
        await Migrations().MigrateAsync();
        var settings = new SiteSettingsStore(_factory);
        Assert.Null(await settings.GetLocationAsync());
        Assert.Equal(TemperatureUnits.Fahrenheit, await settings.GetTemperatureUnitAsync());

        var changed = 0;
        settings.LocationChanged += () => changed++;
        await settings.SetLocationAsync(new SiteLocation("Home", 30.27, -97.74, 150, "America/Chicago"));
        await settings.SetTemperatureUnitAsync(TemperatureUnits.Celsius);
        Assert.Equal(1, changed);
        Assert.Equal("Home", (await settings.GetLocationAsync())!.Name);
        Assert.Equal(TemperatureUnits.Celsius, await settings.GetTemperatureUnitAsync());

        var weather = new FakeWeather();
        var store = Store();
        await using var registry = Registry(new FakeDevicePluginType());
        var catalog = new PluginCatalog(_factory, registry, [weather], [], []);
        var days = new DaySummaryStore(_factory, _time);
        var site = new SiteService(settings, catalog, days, store, _time, NullLogger<SiteService>.Instance);

        // T0 = 2026-11-07 20:00 UTC = 14:00 CST. One hour of solar at a steady 1,000 W, from 13:00 to 14:00 local.
        var day = new DateOnly(2026, 11, 7);
        var solar = new DeviceInfo("fake-a", "Test", "Fake", null, null);
        for (var s = 0; s < 3600; s += 10)
            await store.SaveAsync(solar, [new Reading("fake-a", Metrics.PvPower, 1000, "W", T0.AddHours(-1).AddSeconds(s))]);

        // Weather is fetched twice at the same observation time: logged once.
        weather.Report = FakeWeather.At(T0.AddHours(-1), 18.5);
        _time.Now = T0.AddHours(-1).AddMinutes(1);
        await site.RefreshWeatherAsync();
        weather.Report = FakeWeather.At(T0, 21.0);
        _time.Now = T0.AddMinutes(5);
        await site.RefreshWeatherAsync();
        await site.RefreshWeatherAsync();
        var temps = await store.GetHistoryAsync(SiteDevices.Weather, WeatherMetrics.Temperature, T0.AddHours(-2), T0.AddHours(1), HistoryResolution.Raw);
        Assert.Equal([18.5, 21.0], temps.Points.Select(p => p.Value));

        // Today's forecast was missing, so it was filled in; tomorrow's (before the day starts) keeps updating.
        var today = await days.GetAsync(day);
        Assert.Equal(22.0, today!.ForecastHighC);
        weather.Report = FakeWeather.At(T0.AddMinutes(15), 19.0, todayHigh: 30, tomorrowHigh: 15);
        _time.Now = T0.AddMinutes(16);
        await site.RefreshWeatherAsync();
        Assert.Equal(22.0, (await days.GetAsync(day))!.ForecastHighC);                // frozen once the day started
        Assert.Equal(15.0, (await days.GetAsync(day.AddDays(1)))!.ForecastHighC);     // still updating

        // Summaries: sun times, actual temperatures, energy (from minute summaries).
        await Maintenance().RunOnceAsync();
        await site.UpdateDaySummariesAsync();
        today = await days.GetAsync(day);
        Assert.NotNull(today!.Sunrise);
        Assert.Equal(21.0, today.ActualHighC);
        Assert.Equal(18.5, today.ActualLowC);
        Assert.InRange(today.SolarKwh!.Value, 0.95, 1.01);
        Assert.Null(today.GridKwh);
        Assert.True(today.ClearSkyIrradiationKwhM2 > 4);

        var range = await days.GetRangeAsync(day.AddDays(-1), day.AddDays(1));
        Assert.Contains(range, d => d.Date == day);
        Assert.Equal(range.OrderBy(d => d.Date), range);

        var overview = await site.GetOverviewAsync();
        Assert.Equal("Home", overview.Location!.Name);
        Assert.Equal(19.0, overview.Weather!.TemperatureC);
        Assert.Equal(1015.2, overview.Pressure!.Hpa);   // logged as weather.pressure and read back
        Assert.Null(overview.Pressure.ChangeHpa3h);    // not 3 hours of history yet
        Assert.Equal(day, overview.Today!.Date);
        Assert.Contains(overview.Insights, i => i.Kind == InsightKinds.SolarOutlook);

        // Disabling the weather plugin stops weather (the cached report is dropped on the next refresh).
        await catalog.SetEnabledAsync(FakeWeather.PluginId, false);
        await site.RefreshWeatherAsync();
        Assert.Null(site.LastWeather);
        Assert.Contains(await catalog.GetPluginsAsync(), p => p.Id == FakeWeather.PluginId && !p.Enabled && p.Kind == PluginKinds.Service);
    }

    [Fact]
    public async Task PluginSettings_SecretsAreWriteOnly_AndValidated()
    {
        await Migrations().MigrateAsync();
        var store = new PluginSettingsStore(_factory);
        var plugin = new FakeNotifier();

        var info = await store.GetInfoAsync(plugin);
        Assert.False(info.IsConfigured);
        Assert.Equal("Send test", info.TestLabel);

        info = await store.SaveAsync(plugin, new SavePluginSettingsRequest(new() { ["target"] = " me ", ["token"] = "s3cret", ["mode"] = "fast" }));
        Assert.True(info.IsConfigured);
        Assert.Equal("me", info.Values["target"]);
        Assert.Null(info.Values["token"]);                       // never returned
        Assert.Equal(["token"], info.SecretsSet);

        // null keeps the saved secret, "" clears it, a value replaces it.
        await store.SaveAsync(plugin, new SavePluginSettingsRequest(new() { ["target"] = "me", ["token"] = null }));
        Assert.Equal("s3cret", (await store.GetValuesAsync(plugin.Id))["token"]);
        await store.SaveAsync(plugin, new SavePluginSettingsRequest(new() { ["target"] = "me", ["token"] = "new" }));
        Assert.Equal("new", (await store.GetValuesAsync(plugin.Id))["token"]);
        await store.SaveAsync(plugin, new SavePluginSettingsRequest(new() { ["target"] = "me", ["token"] = "" }));
        Assert.False((await store.GetValuesAsync(plugin.Id)).ContainsKey("token"));

        // Defaults fill in for what plugins receive; invalid numbers and choices are rejected.
        Assert.Equal("slow", (await store.GetEffectiveAsync(plugin))["mode"]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(plugin, new SavePluginSettingsRequest(new() { ["target"] = "me", ["mode"] = "warp" })));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(plugin, new SavePluginSettingsRequest(new() { ["target"] = "me", ["count"] = "many" })));

        // Clearing a required field leaves the plugin "not set up".
        Assert.False((await store.SaveAsync(plugin, new SavePluginSettingsRequest(new() { ["target"] = "" }))).IsConfigured);
    }

    [Fact]
    public async Task Notifications_SendOnce_OfflineAndBack_WeatherAlerts()
    {
        await Migrations().MigrateAsync();
        var settings = new SiteSettingsStore(_factory);
        await settings.SetLocationAsync(new SiteLocation("Home", 30.27, -97.74, null, "America/Chicago"));

        var notifier = new FakeNotifier();
        var alerts = new FakeAlerts();
        var store = Store();
        await using var registry = Registry(new FakeDevicePluginType());
        await registry.InitializeAsync(EmptyConfig());
        var catalog = new PluginCatalog(_factory, registry, [], [alerts], [notifier]);
        var pluginSettings = new PluginSettingsStore(_factory);
        var site = new SiteService(settings, catalog, new DaySummaryStore(_factory, _time), store, _time, NullLogger<SiteService>.Instance);
        var service = new NotificationService(_factory, catalog, pluginSettings, registry, store, site, new GridMonitor(_factory, store, _time), _time, NullLogger<NotificationService>.Instance);

        await registry.AddAsync(new AddDeviceRequest("Garage", "fake", new() { ["id"] = "a" }));
        await store.SaveAsync(new DeviceInfo("fake-a", "Test", "Fake", null, null), [new Reading("fake-a", Metrics.PvPower, 1, "W", T0)]);
        alerts.Alerts = [new WeatherAlert("nws-1", "Tornado Warning", "Tornado warning until 9 PM", "Extreme", "Immediate", T0, T0.AddHours(2), "Take shelter.", null, "NWS")];
        await site.RefreshAlertsAsync();

        // No channel set up yet: alerts are recorded (and shown as waiting) but nothing is sent.
        _time.Now = T0.AddMinutes(30);
        await service.RunOnceAsync();
        Assert.Empty(notifier.Sent);
        Assert.False((await service.SendTestAsync()).Ok);
        var page = await service.GetAlertsAsync();
        Assert.Equal(2, page.History.Count);
        Assert.All(page.History, a => Assert.Equal(AlertDeliveryStatus.Waiting, a.Status));
        Assert.Equal(2, page.Active.Count);
        Assert.Contains(page.Active, a => a.Title == "Garage is offline" && a.Category == AlertCategories.Device);

        // Once a channel is set up, the waiting alerts go out (once), and tests are recorded too.
        await pluginSettings.SaveAsync(notifier, new SavePluginSettingsRequest(new() { ["target"] = "me" }));
        Assert.True((await service.SendTestAsync()).Ok);
        notifier.Sent.Clear();
        await service.RunOnceAsync();
        await service.RunOnceAsync();
        Assert.Equal(2, notifier.Sent.Count);
        Assert.Contains(notifier.Sent, a => a.Title == "Garage is offline" && a.Severity == AlertSeverity.Critical);
        Assert.Contains(notifier.Sent, a => a.Title == "Tornado Warning" && a.Message.Contains("Take shelter."));
        Assert.Equal("me", notifier.LastSettings!["target"]);

        page = await service.GetAlertsAsync();
        Assert.Equal(3, page.History.Count);
        Assert.Equal(AlertCategories.Test, page.History[0].Category);   // newest first
        Assert.All(page.History, a => Assert.Equal(AlertDeliveryStatus.Sent, a.Status));
        Assert.Contains("Fake notifier: sent", page.History.First(a => a.Title == "Garage is offline").Delivery);
        Assert.Equal("critical", page.History.First(a => a.Title == "Tornado Warning").Severity);
        Assert.Single((await service.GetAlertsAsync(category: AlertCategories.Weather)).History);

        // The device reports again: one "back online", once; the condition is no longer active.
        await store.SaveAsync(new DeviceInfo("fake-a", "Test", "Fake", null, null), [new Reading("fake-a", Metrics.PvPower, 1, "W", _time.Now)]);
        await service.RunOnceAsync();
        await service.RunOnceAsync();
        Assert.Equal(3, notifier.Sent.Count);
        Assert.Equal("Garage is back online", notifier.Sent[^1].Title);
        page = await service.GetAlertsAsync();
        Assert.DoesNotContain(page.Active, a => a.Category == AlertCategories.Device);
        Assert.Equal("Garage is back online", page.History[0].Title);

        // Paging.
        var first = await service.GetAlertsAsync(limit: 2);
        Assert.True(first.HasMore);
        var rest = await service.GetAlertsAsync(limit: 2, beforeId: first.History[^1].Id);
        Assert.Equal(2, rest.History.Count);
        Assert.False(rest.HasMore);

        // Turning a category off stops it; a failing channel is retried later, not every minute.
        await service.SetPreferencesAsync(new NotificationPreferences(WeatherAlerts: false));
        alerts.Alerts = [new WeatherAlert("nws-2", "Flood Watch", null, "Moderate", null, null, null, null, null, null)];
        await site.RefreshAlertsAsync();
        await service.RunOnceAsync();
        Assert.Equal(3, notifier.Sent.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetPreferencesAsync(new NotificationPreferences(OfflineMinutes: 1)));

        await service.SetPreferencesAsync(new NotificationPreferences());
        notifier.Fail = true;
        await service.RunOnceAsync();
        var flood = (await service.GetAlertsAsync()).History.First(a => a.Title == "Flood Watch");
        Assert.Equal(AlertDeliveryStatus.Failed, flood.Status);
        Assert.Contains("Fake delivery failure", flood.Delivery);
        notifier.Fail = false;
        await service.RunOnceAsync();                 // still within the retry pause
        Assert.Equal(3, notifier.Sent.Count);
        _time.Now = _time.Now.AddMinutes(16);
        await store.SaveAsync(new DeviceInfo("fake-a", "Test", "Fake", null, null), [new Reading("fake-a", Metrics.PvPower, 1, "W", _time.Now)]);
        await service.RunOnceAsync();
        Assert.Equal("Flood Watch", notifier.Sent[^1].Title);
        Assert.Equal(AlertDeliveryStatus.Sent, (await service.GetAlertsAsync()).History.First(a => a.Title == "Flood Watch").Status);

        // Undeliverable for more than 24 hours: given up on (not sent) and not retried.
        notifier.Fail = true;
        alerts.Alerts = [new WeatherAlert("nws-3", "Heat Advisory", null, "Minor", null, null, null, null, null, null)];
        await site.RefreshAlertsAsync();
        await service.RunOnceAsync();
        _time.Now = _time.Now.AddHours(25);
        notifier.Fail = false;
        var sentBefore = notifier.Sent.Count;
        await store.SaveAsync(new DeviceInfo("fake-a", "Test", "Fake", null, null), [new Reading("fake-a", Metrics.PvPower, 1, "W", _time.Now)]);
        await service.RunOnceAsync();
        Assert.DoesNotContain(notifier.Sent.Skip(sentBefore), a => a.Title == "Heat Advisory");
        Assert.Equal(AlertDeliveryStatus.NotSent, (await service.GetAlertsAsync()).History.First(a => a.Title == "Heat Advisory").Status);
    }

    private sealed class FakeNotifier : INotificationPlugin, IConfigurablePlugin
    {
        public List<Alert> Sent { get; } = [];
        public IReadOnlyDictionary<string, string?>? LastSettings { get; private set; }
        public bool Fail { get; set; }
        public string Id => "fake-notify";
        public string DisplayName => "Fake notifier";
        public string Description => "Test";
        public string? TestLabel => "Send test";

        public IReadOnlyList<SettingField> PluginSettings { get; } =
        [
            new("target", "Target"),
            new("token", "Token", SettingKind.Secret, Required: false),
            new("mode", "Mode", SettingKind.Choice, Required: false, Default: "slow", Options: ["slow", "fast"]),
            new("count", "Count", SettingKind.Number, Required: false),
        ];

        public Task SendAsync(Alert alert, IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Fake delivery failure.");
            Sent.Add(alert);
            LastSettings = settings;
            return Task.CompletedTask;
        }

        public Task<PluginActionResult> TestAsync(IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginActionResult(true, "ok"));
    }

    private sealed class FakeAlerts : IWeatherAlertProvider
    {
        public IReadOnlyList<WeatherAlert> Alerts { get; set; } = [];
        public string Id => "fake-alerts";
        public string DisplayName => "Fake alerts";
        public string Description => "Test";
        public Task<IReadOnlyList<WeatherAlert>> GetAlertsAsync(SiteLocation location, CancellationToken cancellationToken = default) => Task.FromResult(Alerts);
    }

    private sealed class FakeWeather : IWeatherProvider
    {
        public const string PluginId = "fake-weather";
        public WeatherReport Report { get; set; } = At(DateTimeOffset.UnixEpoch, 0);
        public string Id => PluginId;
        public string DisplayName => "Fake weather";
        public string Description => "Test";
        public Task<WeatherReport> GetWeatherAsync(SiteLocation location, CancellationToken cancellationToken = default) => Task.FromResult(Report);

        public static WeatherReport At(DateTimeOffset time, double temperature, double todayHigh = 22, double tomorrowHigh = 18) =>
            new("Fake", new WeatherNow(time, temperature, 50, 20, 5, 0, 300, 1, true, 1015.2), [],
                [new DailyForecast(new DateOnly(2026, 11, 7), todayHigh, 12, 3.5, 10, 1),
                 new DailyForecast(new DateOnly(2026, 11, 8), tomorrowHigh, 10, 4.0, 10, 1)], time);
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
    public async Task DatabaseDescription_NamesTheServerAndVersion()
    {
        var description = await DatabaseDescription.DescribeAsync(_factory, _settings, CancellationToken.None);
        var expectedPrefix = _settings.Driver switch
        {
            DbDriver.Sqlite => "SQLite ",
            DbDriver.SqlServer => "SQL Server 2022 (16.",
            _ => DriverName == "mariadb" ? "MariaDB 11." : "MySQL 8.",
        };
        Assert.StartsWith(expectedPrefix, description);
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

    [Fact]
    public async Task DeviceFaults_RaiseOneAlertPerCode_AndClearWhenGone()
    {
        await Migrations().MigrateAsync();
        var settings = new SiteSettingsStore(_factory);
        var notifier = new FakeNotifier();
        var store = Store();
        await using var registry = Registry(new FakeDevicePluginType());
        await registry.InitializeAsync(EmptyConfig());
        var catalog = new PluginCatalog(_factory, registry, [], [], [notifier]);
        var pluginSettings = new PluginSettingsStore(_factory);
        var site = new SiteService(settings, catalog, new DaySummaryStore(_factory, _time), store, _time, NullLogger<SiteService>.Instance);
        var grid = new GridMonitor(_factory, store, _time);
        var service = new NotificationService(_factory, catalog, pluginSettings, registry, store, site, grid, _time, NullLogger<NotificationService>.Instance);
        await pluginSettings.SaveAsync(notifier, new SavePluginSettingsRequest(new() { ["target"] = "me" }));
        await service.SetPreferencesAsync(new NotificationPreferences(WeatherAlerts: false, SolarUnderperformance: false));

        await registry.AddAsync(new AddDeviceRequest("Inverter", "fake", new() { ["id"] = "a" }));
        var device = new DeviceInfo("fake-a", "Test", "Fake", null, null);
        async Task Poll(DateTimeOffset at, params int[] codes)
        {
            var readings = Enumerable.Range(0, 4)
                .Select(i => new Reading("fake-a", Metrics.ForString(Metrics.FaultCode, i + 1), i < codes.Length ? codes[i] : 0, "", at))
                .Append(new Reading("fake-a", Metrics.ForPhase(Metrics.GridVoltage, 1), 121, "V", at))
                .Append(new Reading("fake-a", Metrics.ForPhase(Metrics.GridVoltage, 2), 121, "V", at))
                .ToList();
            await store.SaveAsync(device, readings);
            _time.Now = at.AddSeconds(5);
            await service.RunOnceAsync();
        }

        var start = T0.AddMinutes(5);
        await Poll(start);
        Assert.Empty(notifier.Sent);

        // Two faults at once: one alert each, named by the plugin where it can.
        await Poll(start.AddSeconds(10), 9, 41);
        Assert.Equal(2, notifier.Sent.Count);
        Assert.Equal("Inverter: fault 09: PV voltage too high", notifier.Sent[0].Title);
        Assert.Equal(AlertSeverity.Critical, notifier.Sent[0].Severity);
        Assert.Equal("Inverter: fault 41", notifier.Sent[1].Title);
        Assert.Contains("no description", notifier.Sent[1].Message);

        // Still there: no repeats. Then 41 clears.
        await Poll(start.AddSeconds(20), 41, 9);
        Assert.Equal(2, notifier.Sent.Count);
        await Poll(start.AddSeconds(30), 9);
        Assert.Equal(3, notifier.Sent.Count);
        Assert.Equal("Inverter: fault 41 cleared", notifier.Sent[^1].Title);

        // The device goes quiet: stale readings neither clear nor raise anything.
        _time.Now = start.AddMinutes(15);
        await service.RunOnceAsync();
        Assert.Equal(4, notifier.Sent.Count); // the offline alert
        Assert.Contains("offline", notifier.Sent[^1].Title);

        // Back with no faults: 09 clears.
        await Poll(start.AddMinutes(16));
        Assert.Contains(notifier.Sent, a => a.Title == "Inverter: fault 09: PV voltage too high cleared");

        // Turned off: nothing raised.
        await service.SetPreferencesAsync(new NotificationPreferences(WeatherAlerts: false, SolarUnderperformance: false, DeviceFaults: false));
        var before = notifier.Sent.Count;
        await Poll(start.AddMinutes(17), 14);
        Assert.Equal(before, notifier.Sent.Count);
    }

    [Fact]
    public async Task GridOutage_RaisesAlerts_RecordsEvents_AndSuppressesOffline()
    {
        await Migrations().MigrateAsync();
        var settings = new SiteSettingsStore(_factory);
        var notifier = new FakeNotifier();
        var store = Store();
        await using var registry = Registry(new FakeDevicePluginType());
        await registry.InitializeAsync(EmptyConfig());
        var catalog = new PluginCatalog(_factory, registry, [], [], [notifier]);
        var pluginSettings = new PluginSettingsStore(_factory);
        var site = new SiteService(settings, catalog, new DaySummaryStore(_factory, _time), store, _time, NullLogger<SiteService>.Instance);
        var grid = new GridMonitor(_factory, store, _time);
        var service = new NotificationService(_factory, catalog, pluginSettings, registry, store, site, grid, _time, NullLogger<NotificationService>.Instance);
        await pluginSettings.SaveAsync(notifier, new SavePluginSettingsRequest(new() { ["target"] = "me" }));
        await service.SetPreferencesAsync(new NotificationPreferences(WeatherAlerts: false, SolarUnderperformance: false));

        await registry.AddAsync(new AddDeviceRequest("Inverter", "fake", new() { ["id"] = "a" }));
        var device = new DeviceInfo("fake-a", "Test", "Fake", null, null);
        async Task Poll(DateTimeOffset at, double volts) => await store.SaveAsync(device,
        [
            new Reading("fake-a", Metrics.ForPhase(Metrics.GridVoltage, 1), volts, "V", at),
            new Reading("fake-a", Metrics.ForPhase(Metrics.GridVoltage, 2), volts, "V", at),
        ]);

        var start = T0.AddMinutes(5);
        for (var i = 0; i < 6; i++) await Poll(start.AddSeconds(i * 10), 123.4);
        // The grid drops: three zero readings, then the inverter goes quiet (night, no battery).
        for (var i = 6; i < 9; i++) await Poll(start.AddSeconds(i * 10), 0.3);
        _time.Now = start.AddMinutes(2);
        await service.RunOnceAsync();

        var outage = Assert.Single(notifier.Sent);
        Assert.Equal("Grid power is out", outage.Title);
        Assert.Equal(AlertSeverity.Critical, outage.Severity);
        var history = await grid.GetHistoryAsync(30);
        var e = Assert.Single(history.Events);
        Assert.Equal(GridEventKinds.Outage, e.Kind);
        Assert.Equal(start.AddSeconds(60), e.StartedAt);
        Assert.Null(e.EndedAt);

        // Half an hour later the device is still silent: no "offline" alert on top of the outage.
        _time.Now = start.AddMinutes(32);
        await service.RunOnceAsync();
        Assert.Single(notifier.Sent);

        // A fresh monitor (as after a restart) picks the open outage up again; power returns.
        grid = new GridMonitor(_factory, store, _time);
        service = new NotificationService(_factory, catalog, pluginSettings, registry, store, site, grid, _time, NullLogger<NotificationService>.Instance);
        var back = start.AddMinutes(45);
        await Poll(back, 122.9);
        await Poll(back.AddSeconds(10), 123.1);
        _time.Now = back.AddSeconds(30);
        await service.RunOnceAsync();

        Assert.Equal(2, notifier.Sent.Count);
        Assert.Equal("Grid power is back", notifier.Sent[^1].Title);
        Assert.Contains("44 min", notifier.Sent[^1].Message);
        history = await grid.GetHistoryAsync(30);
        e = Assert.Single(history.Events);
        Assert.Equal(back, e.EndedAt);
        Assert.Equal(1, history.Outages);
        Assert.Equal(44, history.OutageMinutes);
        Assert.Contains((await service.GetAlertsAsync(category: AlertCategories.Grid)).History, a => a.Title == "Grid power is back");

        // Running again doesn't duplicate anything.
        await service.RunOnceAsync();
        Assert.Equal(2, notifier.Sent.Count);
        Assert.Single((await grid.GetHistoryAsync(30)).Events);
    }

    [Fact]
    public async Task Reports_TotalByYearMonthAndDay_AndCompareWithAYearEarlier()
    {
        await Migrations().MigrateAsync();
        var settings = new SiteSettingsStore(_factory);
        await settings.SetLocationAsync(new SiteLocation("Home", 33.75, -85.69, null, "America/Chicago"));
        var days = new DaySummaryStore(_factory, _time);
        var savings = new SavingsService(_factory, days, settings, _time);
        var reports = new ReportService(days, savings, settings, _time);
        _time.Now = new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero); // Nov 8 local

        async Task Day(int y, int m, int d, double solar, double load, double grid) =>
            await days.UpdateAsync(new DateOnly(y, m, d), r => { r.SolarWh = solar * 1000; r.LoadWh = load * 1000; r.GridWh = grid * 1000; });
        await Day(2025, 11, 3, 10, 40, 30);
        await Day(2025, 11, 20, 50, 50, 0);   // after Nov 8: not in "same period last year"
        await Day(2026, 10, 15, 30, 40, 10);
        await Day(2026, 11, 2, 20, 40, 20);
        await Day(2026, 11, 8, 40, 40, 0);
        await days.UpdateAsync(new DateOnly(2026, 11, 5), r => r.ForecastHigh = 20); // weather only: not counted

        // Year view: 12 months, the same months a year earlier, the same period last year.
        var year = await reports.GetAsync(2026, null);
        Assert.Equal(ReportScopes.Year, year.Scope);
        Assert.Null(year.Currency);
        Assert.Equal(12, year.Periods.Count);
        Assert.Equal(3, year.Total.Days);
        Assert.Equal(90, year.Total.SolarKwh);
        Assert.Equal(120, year.Total.LoadKwh);
        Assert.Equal(75, year.Total.SelfPoweredPercent);
        Assert.Equal(60, year.Periods[10].SolarKwh);
        Assert.Equal(2, year.Periods[10].Days);
        Assert.Equal(0, year.Periods[0].Days);
        Assert.Null(year.Periods[0].SolarKwh);
        Assert.Equal(60, year.PreviousPeriods[10]!.SolarKwh); // all of Nov 2025
        Assert.Null(year.PreviousPeriods[9]);
        Assert.Equal(10, year.Previous!.SolarKwh);            // 2025 up to Nov 8 only
        Assert.Equal(new DateOnly(2026, 11, 8), year.BestDay!.Date);
        Assert.Equal([2025, 2026], year.Years);

        // Month view: a period per day; with a price, money saved too.
        await savings.SaveSettingsAsync(new SavingsSettings("$", [new ElectricityRate(null, 0.10)]));
        var month = await reports.GetAsync(2026, 11);
        Assert.Equal(30, month.Periods.Count);
        Assert.Equal("$", month.Currency);
        Assert.Equal(6.00, month.Total.Saved);
        Assert.Equal(2.00, month.Periods[1].Saved);
        Assert.Equal(1.00, month.Previous!.Saved);
        Assert.Empty(month.PreviousPeriods);

        // All years: one period per year, no comparison.
        var all = await reports.GetAsync(null, null);
        Assert.Equal(2, all.Periods.Count);
        Assert.Equal(60, all.Periods[0].SolarKwh);
        Assert.Equal(150, all.Total.SolarKwh);
        Assert.Null(all.Previous);
        Assert.Equal(new DateOnly(2025, 11, 3), all.Total.Start);

        await Assert.ThrowsAsync<ArgumentException>(() => reports.GetAsync(null, 11));
        await Assert.ThrowsAsync<ArgumentException>(() => reports.GetAsync(2026, 13));
    }

    [Fact]
    public async Task Savings_SummarizeDays_WithRateHistory_AndPayback()
    {
        await Migrations().MigrateAsync();
        var settings = new SiteSettingsStore(_factory);
        await settings.SetLocationAsync(new SiteLocation("Home", 33.75, -85.69, null, "America/Chicago"));
        var days = new DaySummaryStore(_factory, _time);
        var savings = new SavingsService(_factory, days, settings, _time);
        _time.Now = new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero); // Nov 8 local

        await days.UpdateAsync(new DateOnly(2026, 10, 31), r => r.SolarWh = 20_000);
        await days.UpdateAsync(new DateOnly(2026, 11, 7), r => r.SolarWh = 30_000);
        await days.UpdateAsync(new DateOnly(2026, 11, 8), r => r.SolarWh = 10_000);

        // No price yet: energy only.
        var summary = await savings.GetSummaryAsync();
        Assert.False(summary.Configured);
        Assert.Null(summary.Lifetime);
        Assert.Equal(60, summary.LifetimeSolarKwh);
        Assert.Equal(new DateOnly(2026, 10, 31), summary.Since);

        // $0.10 from the start, $0.20 from Nov 8; system cost $100.
        await savings.SaveSettingsAsync(new SavingsSettings("$",
            [new ElectricityRate(new DateOnly(2026, 11, 8), 0.20), new ElectricityRate(null, 0.10)], SystemCost: 100));
        summary = await savings.GetSummaryAsync();
        Assert.True(summary.Configured);
        Assert.Equal(2.00, summary.Today);       // 10 kWh × 0.20
        Assert.Equal(5.00, summary.ThisMonth);   // 30 × 0.10 + 10 × 0.20
        Assert.Equal(7.00, summary.Lifetime);    // + 20 × 0.10
        Assert.Equal(7.0, summary.PaybackPercent);
        Assert.Equal(0.20, summary.CurrentPricePerKwh);
        Assert.Equal(2, summary.Months.Count);
        Assert.Equal(new MonthSavings(2026, 10, 20, 2), summary.Months[0]);

        // Rates come back sorted, and bad input is refused.
        var saved = await savings.GetSettingsAsync();
        Assert.Null(saved.RateList[0].From);
        await Assert.ThrowsAsync<ArgumentException>(() => savings.SaveSettingsAsync(new SavingsSettings("$", [new ElectricityRate(null, 0)])));
        await Assert.ThrowsAsync<ArgumentException>(() => savings.SaveSettingsAsync(new SavingsSettings("dollars", [])));
    }

    // ---- Backups -------------------------------------------------------------------------------------------

    private static readonly SecretKeys TestSecrets = new(new Dictionary<string, IReadOnlySet<string>>
    {
        ["email-smtp"] = new HashSet<string> { "password" },
        ["fake"] = new HashSet<string> { "secret" },
    });

    private long Schema() => Migrations().GetScripts().Max(s => s.Number);

    private async Task<string> WriteBackupAsync(IDbContextFactory<SolPnlDb> factory, DbSettings settings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"solpnl-backup-test-{Guid.NewGuid():N}.zip");
        await using var file = File.Create(path);
        await BackupArchive.WriteAsync(factory, settings, Schema(), TestSecrets, file, _time, CancellationToken.None);
        return path;
    }

    /// <summary>A device with readings, settings (with secrets), an alert, a day summary and a backup schedule.</summary>
    private async Task SeedAsync(IDbContextFactory<SolPnlDb> factory, string password, string schedule)
    {
        var store = new EfReadingStore(factory, Options.Create(_retention), _time);
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 1000, "W", T0), R(Metrics.LoadPower, 600, "W", T0)]);
        await store.SaveAsync(Inverter, [R(Metrics.PvPower, 1200.5, "W", T0.AddSeconds(10))]);

        await using var db = await factory.CreateDbContextAsync();
        db.DeviceConfigs.Add(new DeviceConfigEntity
        {
            Name = "Garage", PluginId = "fake", DeviceKey = "fake-a", CreatedAtMs = 1,
            SettingsJson = "{\"id\":\"a\",\"secret\":\"device-pass\"}",
        });
        db.PluginSettings.Add(new PluginSettingEntity { PluginId = "email-smtp", Key = "host", Value = "smtp.example.com" });
        db.PluginSettings.Add(new PluginSettingEntity { PluginId = "email-smtp", Key = "password", Value = password });
        db.AppSettings.Add(new AppSettingEntity { Key = BackupService.SettingsKey, Value = schedule });
        db.AlertHistory.Add(new AlertHistoryEntity
        {
            EventKey = "offline:x", Category = "device", Severity = "critical", Title = "Inverter is offline",
            Message = AlertText, RaisedMs = 5,
        });
        db.DaySummaries.Add(new DaySummaryEntity { Day = "2026-11-07", SolarWh = 12345.5, ForecastHigh = null, UpdatedMs = 9 });
        await db.SaveChangesAsync();
    }

    private const string AlertText = "Line one\nLine two \"quoted\" ✓";

    [Fact]
    public async Task Backup_RestoresEverything_ButNeverWritesSecrets()
    {
        await Migrations().MigrateAsync();
        await SeedAsync(_factory, password: "old-pass", schedule: "from-backup");
        var path = await WriteBackupAsync(_factory, _settings);
        try
        {
            // Secrets are left out of the file.
            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            {
                var all = string.Join("\n", zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
                Assert.DoesNotContain("old-pass", all);
                Assert.DoesNotContain("device-pass", all);
                Assert.Contains("smtp.example.com", all);
                var manifest = BackupArchive.ReadManifest(zip, Schema());
                Assert.Equal(3, manifest.Tables["reading"]);
                Assert.False(manifest.SecretsIncluded);
            }

            // Change things after the backup: new secrets, schedule and readings; delete the alert and the day.
            await using (var db = await _factory.CreateDbContextAsync())
            {
                (await db.PluginSettings.SingleAsync(s => s.Key == "password")).Value = "current-pass";
                (await db.DeviceConfigs.SingleAsync()).SettingsJson = "{\"id\":\"a\",\"secret\":\"current-device-pass\"}";
                (await db.AppSettings.SingleAsync(s => s.Key == BackupService.SettingsKey)).Value = "on-this-machine";
                db.AlertHistory.RemoveRange(db.AlertHistory);
                db.DaySummaries.RemoveRange(db.DaySummaries);
                await db.SaveChangesAsync();
            }
            await Store().SaveAsync(Inverter, [R(Metrics.PvPower, 1, "W", T0.AddSeconds(20))]);

            await BackupArchive.RestoreAsync(_factory, _settings, Schema(), TestSecrets, path, CancellationToken.None);

            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(3, await db.Readings.CountAsync());
                Assert.Equal(AlertText, (await db.AlertHistory.SingleAsync()).Message);
                Assert.Equal(12345.5, (await db.DaySummaries.SingleAsync()).SolarWh);
                Assert.Equal("smtp.example.com", (await db.PluginSettings.SingleAsync(s => s.Key == "host")).Value);
                // Secrets and the schedule on this machine are kept.
                Assert.Equal("current-pass", (await db.PluginSettings.SingleAsync(s => s.Key == "password")).Value);
                Assert.Contains("current-device-pass", (await db.DeviceConfigs.SingleAsync()).SettingsJson);
                Assert.Equal("on-this-machine", (await db.AppSettings.SingleAsync(s => s.Key == BackupService.SettingsKey)).Value);
            }

            // New ids carry on after the restored ones (identity/auto-increment counters are right).
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var maxId = await db.AlertHistory.MaxAsync(a => a.Id);
                var added = new AlertHistoryEntity { EventKey = "k", Category = "test", Severity = "info", Title = "t", Message = "m", RaisedMs = 6 };
                db.AlertHistory.Add(added);
                await db.SaveChangesAsync();
                Assert.True(added.Id > maxId);
            }
            await Store().SaveAsync(new DeviceInfo("other", "v", "m", null, null), [R(Metrics.PvPower, 2, "W", T0)]);
            Assert.Equal(2, (await Store().GetDevicesAsync()).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Backup_FromSqlite_RestoresIntoThisDatabase()
    {
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"solpnl-source-{Guid.NewGuid():N}.db");
        var sourceSettings = new DbSettings(DbDriver.Sqlite, $"Data Source={sqlitePath};Pooling=false");
        var source = new Factory(sourceSettings);
        string? path = null;
        try
        {
            await new MigrationService(source, sourceSettings, NullLogger<MigrationService>.Instance).MigrateAsync();
            await SeedAsync(source, password: "p", schedule: "s");
            path = await WriteBackupAsync(source, sourceSettings);

            await Migrations().MigrateAsync();
            await BackupArchive.RestoreAsync(_factory, _settings, Schema(), TestSecrets, path, CancellationToken.None);

            await using var db = await _factory.CreateDbContextAsync();
            Assert.Equal(3, await db.Readings.CountAsync());
            Assert.Equal(2, await db.Metrics.CountAsync());
            Assert.Equal("Inverter is offline", (await db.AlertHistory.SingleAsync()).Title);
            // A fresh machine has no secrets to keep, so the password is simply absent.
            Assert.False(await db.PluginSettings.AnyAsync(s => s.Key == "password"));
        }
        finally
        {
            if (path is not null) File.Delete(path);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);
        }
    }

    [Fact]
    public async Task Restore_RefusesNewerBackups_AndFailedRestoresChangeNothing()
    {
        await Migrations().MigrateAsync();
        await SeedAsync(_factory, password: "p", schedule: "s");
        var path = await WriteBackupAsync(_factory, _settings);
        var broken = path + ".broken.zip";
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BackupArchive.RestoreAsync(_factory, _settings, Schema() - 1, TestSecrets, path, CancellationToken.None));
            Assert.Contains("newer version", error.Message);

            // Corrupt the last table: the restore fails part-way and rolls back.
            using (var source = System.IO.Compression.ZipFile.OpenRead(path))
            using (var target = System.IO.Compression.ZipFile.Open(broken, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries)
                {
                    var text = new StreamReader(entry.Open()).ReadToEnd();
                    if (entry.Name == "alert_history.jsonl") text += "{ not json\n";
                    using var writer = new StreamWriter(target.CreateEntry(entry.FullName).Open());
                    writer.Write(text);
                }
            }
            await Store().SaveAsync(Inverter, [R(Metrics.PvPower, 1, "W", T0.AddSeconds(20))]);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                BackupArchive.RestoreAsync(_factory, _settings, Schema(), TestSecrets, broken, CancellationToken.None));

            await using var db = await _factory.CreateDbContextAsync();
            Assert.Equal(4, await db.Readings.CountAsync()); // untouched, including the reading added after the backup
            Assert.Equal(1, await db.AlertHistory.CountAsync());
        }
        finally
        {
            File.Delete(path);
            File.Delete(broken);
        }
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
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
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

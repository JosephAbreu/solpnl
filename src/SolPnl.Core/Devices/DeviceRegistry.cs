using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Data;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Devices;

/// <summary>A configured device that is currently being polled.</summary>
public sealed record ActiveDevice(int ConfigId, string Name, string DeviceKey, IDevicePlugin Plugin);

/// <summary>Thrown for requests that are valid but can't be done in the current state (e.g. duplicate device).</summary>
public sealed class DeviceConfigurationException(string message) : Exception(message);

/// <summary>
/// Owns device configuration (stored in the database) and the running plugin instances built from it.
/// The poller reads <see cref="ActiveDevices"/> on every tick, so adding/removing devices and enabling/disabling
/// plugins takes effect without a restart.
/// </summary>
public sealed class DeviceRegistry : IAsyncDisposable
{
    public const string LegacyImportFlag = "legacy_plugin_config_imported";
    private const int MaxNameLength = 100;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private readonly IDbContextFactory<SolPnlDb> _dbFactory;
    private readonly IReadingStore _store;
    private readonly ILogger<DeviceRegistry> _logger;
    private readonly Dictionary<string, IDevicePluginType> _types;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<int, (string Fingerprint, ActiveDevice Device)> _running = new();
    private volatile IReadOnlyList<ActiveDevice> _active = [];

    public DeviceRegistry(IDbContextFactory<SolPnlDb> dbFactory, IEnumerable<IDevicePluginType> types, IReadingStore store,
        ILogger<DeviceRegistry> logger)
    {
        _dbFactory = dbFactory;
        _store = store;
        _logger = logger;
        _types = new Dictionary<string, IDevicePluginType>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            // Two packages can't both provide a plugin id: the first one wins.
            if (!_types.TryAdd(type.Id, type))
                logger.LogWarning("Device plugin '{Id}' is provided by more than one plugin package; using the first.", type.Id);
        }
    }

    /// <summary>Devices being polled right now (configured, with an installed and enabled plugin).</summary>
    public IReadOnlyList<ActiveDevice> ActiveDevices => _active;

    public IReadOnlyCollection<IDevicePluginType> PluginTypes => _types.Values;

    /// <summary>
    /// What the active devices' vendor calls a PV input (e.g. "line" for SRNE); "string" when there are none, or
    /// devices from vendors with different words are active.
    /// </summary>
    public string PvInputName
    {
        get
        {
            var names = _active
                .Select(d => _types.TryGetValue(d.Plugin.Id, out var t) ? t.PvInputName : "string")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return names.Count == 1 ? names[0] : "string";
        }
    }

    /// <summary>Imports a pre-UI "Plugins" config section once, then starts the configured devices.</summary>
    public async Task InitializeAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await ImportLegacyConfigAsync(configuration, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    // ---- Plugins -------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PluginInfo>> GetPluginsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var states = await db.PluginStates.AsNoTracking().ToDictionaryAsync(s => s.PluginId, s => s.Enabled != 0, cancellationToken);
        var counts = await db.DeviceConfigs.AsNoTracking().GroupBy(d => d.PluginId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

        return _types.Values
            .OrderBy(t => t.DisplayName, StringComparer.CurrentCulture)
            .Select(t => new PluginInfo(t.Id, t.DisplayName, t.Description, PluginKinds.Device,
                states.GetValueOrDefault(t.Id, true), counts.GetValueOrDefault(t.Id), t.Settings,
                PvInputName: t.PvInputName))
            .ToList();
    }

    /// <summary>Enables or disables a plugin. Disabling stops polling all of its devices (their settings are kept).</summary>
    public async Task<PluginInfo> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!_types.ContainsKey(pluginId)) throw new KeyNotFoundException($"No plugin '{pluginId}' is installed.");

        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var state = await db.PluginStates.SingleOrDefaultAsync(s => s.PluginId == pluginId, cancellationToken);
            if (state is null) db.PluginStates.Add(new PluginStateEntity { PluginId = pluginId, Enabled = enabled ? 1 : 0 });
            else state.Enabled = enabled ? 1 : 0;
            await db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("Plugin {Plugin} {State}.", pluginId, enabled ? "enabled" : "disabled");
        await ReloadAsync(cancellationToken);
        return (await GetPluginsAsync(cancellationToken)).Single(p => p.Id == pluginId);
    }

    // ---- Devices -------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ConfiguredDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DeviceConfigs.AsNoTracking().OrderBy(d => d.Name).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>Validates settings, connects and takes one reading, without saving anything.</summary>
    public async Task<DeviceTestResult> TestAsync(string pluginId, IReadOnlyDictionary<string, string?> settings,
        CancellationToken cancellationToken = default)
    {
        if (!_types.TryGetValue(pluginId, out var type))
            return new DeviceTestResult(false, $"No plugin '{pluginId}' is installed.");

        IDevicePlugin plugin;
        try
        {
            plugin = type.Create(Normalize(type, settings));
        }
        catch (ArgumentException ex)
        {
            return new DeviceTestResult(false, ex.Message);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TestTimeout);
            var info = await plugin.GetDeviceInfoAsync(cts.Token);
            var readings = await plugin.ReadAsync(cts.Token);
            return new DeviceTestResult(true, $"Connected. Got {readings.Count} values from the device.", info.DeviceId, readings.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeviceTestResult(false, $"The device didn't answer within {TestTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeviceTestResult(false, ex.GetBaseException().Message);
        }
        finally
        {
            await DisposeAsync(plugin);
        }
    }

    /// <summary>Saves a new device and starts polling it. Throws ArgumentException (invalid) or DeviceConfigurationException.</summary>
    public async Task<ConfiguredDevice> AddAsync(AddDeviceRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Name is required.");
        if (name.Length > MaxNameLength) throw new ArgumentException($"Name must be at most {MaxNameLength} characters.");
        if (!_types.TryGetValue(request.PluginId ?? "", out var type))
            throw new ArgumentException($"No plugin '{request.PluginId}' is installed.");

        var settings = Normalize(type, request.Settings ?? []);
        var plugin = type.Create(settings); // throws ArgumentException for invalid settings
        string deviceKey;
        try
        {
            deviceKey = (await plugin.GetDeviceInfoAsync(cancellationToken)).DeviceId;
        }
        finally
        {
            await DisposeAsync(plugin);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await IsEnabledAsync(db, type.Id, cancellationToken))
                throw new DeviceConfigurationException($"The {type.DisplayName} plugin is disabled. Enable it on the Plugins page first.");

            var existing = await db.DeviceConfigs.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceKey == deviceKey, cancellationToken);
            if (existing is not null)
                throw new DeviceConfigurationException($"This device is already configured as '{existing.Name}'.");

            var entity = new DeviceConfigEntity
            {
                Name = name,
                PluginId = type.Id,
                SettingsJson = JsonSerializer.Serialize(settings),
                DeviceKey = deviceKey,
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            db.DeviceConfigs.Add(entity);
            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Device '{Name}' ({DeviceKey}, {Plugin}) added.", name, deviceKey, type.Id);
            await ReloadCoreAsync(cancellationToken);
            return ToDto(entity);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Stops polling a device and deletes its configuration; optionally its stored history too.</summary>
    public async Task<bool> RemoveAsync(int id, bool deleteHistory, CancellationToken cancellationToken = default)
    {
        string deviceKey;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var entity = await db.DeviceConfigs.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
            if (entity is null) return false;

            deviceKey = entity.DeviceKey;
            db.DeviceConfigs.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Device '{Name}' ({DeviceKey}) removed{History}.", entity.Name, deviceKey,
                deleteHistory ? " with its history" : "; history kept");
            await ReloadCoreAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        if (deleteHistory) await _store.DeleteDeviceAsync(deviceKey, cancellationToken);
        return true;
    }

    // ---- Running instances ---------------------------------------------------------------------------------

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await ReloadCoreAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var configs = await db.DeviceConfigs.AsNoTracking().OrderBy(d => d.Id).ToListAsync(cancellationToken);
        var disabled = await db.PluginStates.AsNoTracking().Where(s => s.Enabled == 0).Select(s => s.PluginId).ToListAsync(cancellationToken);

        var wanted = new HashSet<int>();
        foreach (var config in configs)
        {
            if (disabled.Contains(config.PluginId)) continue;
            if (!_types.TryGetValue(config.PluginId, out var type))
            {
                _logger.LogWarning("Device '{Name}' uses plugin '{Plugin}', which isn't installed; not polling it.", config.Name, config.PluginId);
                continue;
            }

            var fingerprint = $"{config.PluginId}|{config.SettingsJson}|{config.Name}";
            wanted.Add(config.Id);
            if (_running.TryGetValue(config.Id, out var current) && current.Fingerprint == fingerprint) continue;

            try
            {
                var settings = JsonSerializer.Deserialize<Dictionary<string, string?>>(config.SettingsJson) ?? [];
                var device = new ActiveDevice(config.Id, config.Name, config.DeviceKey, type.Create(settings));
                if (_running.Remove(config.Id, out var replaced)) await DisposeAsync(replaced.Device.Plugin);
                _running[config.Id] = (fingerprint, device);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                wanted.Remove(config.Id);
                _logger.LogWarning("Device '{Name}' has invalid settings and won't be polled: {Message}", config.Name, ex.Message);
            }
        }

        foreach (var id in _running.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            _running.Remove(id, out var stopped);
            await DisposeAsync(stopped.Device.Plugin);
        }

        _active = _running.Values.Select(r => r.Device).OrderBy(d => d.ConfigId).ToList();
    }

    private async Task ImportLegacyConfigAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.AppSettings.AnyAsync(s => s.Key == LegacyImportFlag, cancellationToken)) return;

        // Before devices were managed in the UI, the SRNE plugin was configured in appsettings ("Plugins:SolarmanSrne").
        var section = configuration.GetSection("Plugins:SolarmanSrne");
        if (section.Exists() && _types.TryGetValue("solarman-srne", out var type))
        {
            var settings = new Dictionary<string, string?>
            {
                ["host"] = section["Host"],
                ["loggerSerial"] = section["LoggerSerial"],
                ["port"] = section["Port"],
                ["slaveId"] = section["SlaveId"],
            };
            try
            {
                var normalized = Normalize(type, settings);
                var plugin = type.Create(normalized);
                var key = (await plugin.GetDeviceInfoAsync(cancellationToken)).DeviceId;
                await DisposeAsync(plugin);

                if (!await db.DeviceConfigs.AnyAsync(d => d.DeviceKey == key, cancellationToken))
                {
                    db.DeviceConfigs.Add(new DeviceConfigEntity
                    {
                        Name = "Inverter",
                        PluginId = type.Id,
                        SettingsJson = JsonSerializer.Serialize(normalized),
                        DeviceKey = key,
                        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                    _logger.LogInformation("Imported device {DeviceKey} from the Plugins:SolarmanSrne setting. Manage devices on the Devices page.", key);
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("Couldn't import the Plugins:SolarmanSrne setting: {Message}", ex.Message);
            }
        }

        // Only ever import once, so a device removed in the UI doesn't come back from an old config file.
        db.AppSettings.Add(new AppSettingEntity { Key = LegacyImportFlag, Value = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------

    private static async Task<bool> IsEnabledAsync(SolPnlDb db, string pluginId, CancellationToken ct) =>
        await db.PluginStates.Where(s => s.PluginId == pluginId).Select(s => (int?)s.Enabled).SingleOrDefaultAsync(ct) != 0;

    /// <summary>Keeps only the type's known settings, trimmed, dropping empty optional values.</summary>
    private static Dictionary<string, string?> Normalize(IDevicePluginType type, IReadOnlyDictionary<string, string?> settings)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in type.Settings)
        {
            var value = settings.TryGetValue(field.Key, out var v) ? v?.Trim() : null;
            if (!string.IsNullOrEmpty(value)) result[field.Key] = value;
        }
        return result;
    }

    private ConfiguredDevice ToDto(DeviceConfigEntity e)
    {
        var settings = JsonSerializer.Deserialize<Dictionary<string, string?>>(e.SettingsJson) ?? [];
        if (_types.TryGetValue(e.PluginId, out var type))
            foreach (var secret in type.Settings.Where(s => s.Kind == SettingKind.Secret))
                if (settings.ContainsKey(secret.Key)) settings[secret.Key] = null;

        return new ConfiguredDevice(e.Id, e.Name, e.PluginId, e.DeviceKey, settings, DateTimeOffset.FromUnixTimeMilliseconds(e.CreatedAtMs));
    }

    private static async ValueTask DisposeAsync(IDevicePlugin plugin)
    {
        if (plugin is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (plugin is IDisposable disposable) disposable.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, device) in _running.Values) await DisposeAsync(device.Plugin);
        _running.Clear();
        _active = [];
        _lock.Dispose();
    }
}

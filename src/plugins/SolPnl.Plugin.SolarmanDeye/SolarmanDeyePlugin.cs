using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Solarman;

namespace SolPnl.Plugin.SolarmanDeye;

public sealed record SolarmanDeyeOptions
{
    /// <summary>Logger IP address or host name on the LAN.</summary>
    public required string Host { get; init; }

    /// <summary>Logger serial number (from the sticker on the WiFi stick).</summary>
    public required uint LoggerSerial { get; init; }

    /// <summary>US split-phase model (two 120 V legs): report L2 values too.</summary>
    public bool SplitPhase { get; init; }

    public int Port { get; init; } = 8899;
    public byte SlaveId { get; init; } = 1;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Reads Deye single-phase and split-phase hybrid inverters (and rebrands such as Sunsynk) through a Solarman V5
/// WiFi stick logger, entirely over the local network.
/// </summary>
public sealed class SolarmanDeyePlugin : IDevicePlugin, IDeviceFaults, IDeviceState, IAsyncDisposable
{
    private readonly SolarmanDeyeOptions _options;
    private readonly SolarmanV5Client _client;
    private readonly string _deviceId;
    private DeyeIdentity? _identity;

    public SolarmanDeyePlugin(SolarmanDeyeOptions options)
    {
        _options = options;
        _client = new SolarmanV5Client(options.Host, options.LoggerSerial, options.Port, options.Timeout);
        _deviceId = $"deye-{options.LoggerSerial}";
    }

    public string Id => SolarmanDeyePluginType.PluginId;
    public string DisplayName => "Solarman logger + Deye/Sunsynk inverter";

    public string? DescribeFault(int code) => DeyeFaults.Describe(code);

    public string StateMetric => DeyeRegisterMap.StateMetric;

    public string? DescribeState(int value) => DeyeStates.Describe(value);

    /// <summary>Clock reported by the inverter on the last successful read (local time, no time zone).</summary>
    public DateTime? LastInverterClock { get; private set; }

    /// <summary>
    /// Doesn't talk to the inverter (adding a device must work while it's offline); the inverter's serial number and
    /// rated power are filled in after the first successful read.
    /// </summary>
    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        var model = _identity?.RatedPowerWatts is { } watts and > 0
            ? $"Hybrid inverter {watts / 1000:0.#} kW"
            : "Hybrid inverter";
        return Task.FromResult(new DeviceInfo(_deviceId, "Deye-compatible", model,
            _identity?.SerialNumber ?? _options.LoggerSerial.ToString(), FirmwareVersion: null));
    }

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var registers = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in DeyeRegisterMap.Blocks)
        {
            var values = await _client.ReadRegistersAsync(
                _options.SlaveId, ModbusRtu.ReadHoldingRegisters, start, count, cancellationToken);
            for (var i = 0; i < values.Length; i++)
                registers[(ushort)(start + i)] = values[i];
        }

        _identity = DeyeRegisterMap.DecodeIdentity(registers);
        LastInverterClock = DeyeRegisterMap.DecodeClock(registers);
        return DeyeRegisterMap.Decode(registers, _deviceId, DateTimeOffset.UtcNow, _options.SplitPhase);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Plugin.SolarmanSrne.Protocol;

namespace SolPnl.Plugin.SolarmanSrne;

public sealed record SolarmanSrneOptions
{
    /// <summary>Logger IP address or host name on the LAN.</summary>
    public required string Host { get; init; }

    /// <summary>Logger serial number (from the sticker on the WiFi stick).</summary>
    public required uint LoggerSerial { get; init; }

    public int Port { get; init; } = 8899;
    public byte SlaveId { get; init; } = 1;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Reads SRNE-family inverters (e.g. Sungoldpower 10kW split phase) through a Solarman V5 WiFi stick logger,
/// entirely over the local network.
/// </summary>
public sealed class SolarmanSrnePlugin : IDevicePlugin, IDeviceFaults, IDeviceState, IAsyncDisposable
{
    private readonly SolarmanSrneOptions _options;
    private readonly SolarmanV5Client _client;
    private readonly string _deviceId;

    public SolarmanSrnePlugin(SolarmanSrneOptions options)
    {
        _options = options;
        _client = new SolarmanV5Client(options.Host, options.LoggerSerial, options.Port, options.Timeout);
        _deviceId = $"solarman-{options.LoggerSerial}";
    }

    public string Id => "solarman-srne";
    public string DisplayName => "Solarman logger + SRNE-family inverter";

    public string? DescribeFault(int code) => SrneFaults.Describe(code);

    public string StateMetric => SrneRegisterMap.StateMetric;

    public string? DescribeState(int value) => SrneStates.Describe(value);

    /// <summary>Clock reported by the inverter on the last successful read (local time, no time zone).</summary>
    public DateTime? LastInverterClock { get; private set; }

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        // Version registers (0x0014..0x0017) are readable but their format isn't decoded yet.
        Task.FromResult(new DeviceInfo(_deviceId, "SRNE-compatible", "Split-phase hybrid inverter",
            _options.LoggerSerial.ToString(), FirmwareVersion: null));

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var registers = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in SrneRegisterMap.Blocks)
        {
            var values = await _client.ReadRegistersAsync(
                _options.SlaveId, ModbusRtu.ReadHoldingRegisters, start, count, cancellationToken);
            for (var i = 0; i < values.Length; i++)
                registers[(ushort)(start + i)] = values[i];
        }

        LastInverterClock = SrneRegisterMap.DecodeClock(registers);
        return SrneRegisterMap.Decode(registers, _deviceId, DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Luxpower;

public sealed record LuxpowerOptions
{
    /// <summary>WiFi dongle IP address or host name on the LAN.</summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 8000;

    /// <summary>The dongle's ("datalog") serial number: 10 characters.</summary>
    public required string DatalogSerial { get; init; }

    /// <summary>The inverter's serial number: 10 characters.</summary>
    public required string InverterSerial { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Reads an EG4 / Luxpower hybrid inverter through its WiFi dongle, entirely over the local network.</summary>
public sealed class LuxpowerPlugin : IDevicePlugin, IDeviceFaults, IDeviceState, IAsyncDisposable
{
    private readonly LuxpowerOptions _options;
    private readonly LuxpowerClient _client;
    private readonly string _deviceId;

    public LuxpowerPlugin(LuxpowerOptions options)
    {
        _options = options;
        _client = new LuxpowerClient(options.Host, options.Port, options.DatalogSerial, options.InverterSerial, options.Timeout);
        _deviceId = $"luxpower-{options.InverterSerial.ToLowerInvariant()}";
    }

    public string Id => LuxpowerPluginType.PluginId;
    public string DisplayName => "EG4 / Luxpower inverter";

    public string? DescribeFault(int code) => LuxpowerCodes.Describe(code);

    public string StateMetric => LuxpowerRegisterMap.StatusMetric;

    public string? DescribeState(int value) => LuxpowerCodes.DescribeStatus(value);

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInfo(_deviceId, "EG4 / Luxpower", "Hybrid inverter", _options.InverterSerial, FirmwareVersion: null));

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var registers = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in LuxpowerRegisterMap.Blocks)
        {
            var values = await _client.ReadRegistersAsync(LuxpowerClient.ReadInput, start, count, cancellationToken);
            for (var i = 0; i < values.Length; i++) registers[(ushort)(start + i)] = values[i];
        }
        return LuxpowerRegisterMap.Decode(registers, _deviceId, DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

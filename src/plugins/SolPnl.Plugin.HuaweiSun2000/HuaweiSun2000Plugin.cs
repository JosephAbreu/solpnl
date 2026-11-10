using System.Text;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Modbus;

namespace SolPnl.Plugin.HuaweiSun2000;

public sealed record HuaweiSun2000Options
{
    /// <summary>SDongle or inverter WiFi address.</summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;

    /// <summary>The SDongle can be slow to answer.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Reads a Huawei SUN2000 inverter over Modbus TCP, plus the Huawei power meter and LUNA2000 battery behind it
/// when they're there. Identity (model, serial number, number of strings) is read on the first poll.
/// </summary>
public sealed class HuaweiSun2000Plugin : IDevicePlugin, IDeviceFaults, IDeviceState, IAsyncDisposable
{
    private readonly HuaweiSun2000Options _options;
    private readonly ModbusTcpClient _client;
    private readonly string _deviceId;
    private HuaweiIdentity? _identity;

    public HuaweiSun2000Plugin(HuaweiSun2000Options options)
    {
        _options = options;
        _client = new ModbusTcpClient(options.Host, options.Port, options.Timeout);
        _deviceId = DeviceKey(options.Host, options.UnitId);
    }

    /// <summary>"huawei-192-168-1-90-1" for host 192.168.1.90, unit 1: from the settings, letters, digits and dashes only.</summary>
    public static string DeviceKey(string host, byte unitId)
    {
        var sb = new StringBuilder("huawei-");
        foreach (var c in host.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.Append('-').Append(unitId).ToString();
    }

    public string Id => HuaweiSun2000PluginType.PluginId;
    public string DisplayName => "Huawei SUN2000 inverter (Modbus TCP)";

    public string? DescribeFault(int code) => HuaweiCodes.DescribeAlarm(code);

    public string StateMetric => HuaweiRegisterMap.StateMetric;

    public string? DescribeState(int value) => HuaweiCodes.DescribeStatus(value);

    /// <summary>Doesn't talk to the inverter; the model and serial number come from the first read.</summary>
    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInfo(_deviceId, "Huawei", _identity?.Model ?? "SUN2000", _identity?.SerialNumber, FirmwareVersion: null));

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var identity = _identity ??= HuaweiRegisterMap.DecodeIdentity(
            await ReadBlocksAsync(HuaweiRegisterMap.IdentityBlocks, optional: false, cancellationToken));

        var registers = await ReadBlocksAsync(HuaweiRegisterMap.InverterBlocks(identity.PvStrings), optional: false, cancellationToken);

        // The meter and battery are optional: an inverter without them refuses those addresses.
        Merge(registers, await ReadBlocksAsync([HuaweiRegisterMap.MeterBlock], optional: true, cancellationToken));
        Merge(registers, await ReadBlocksAsync(HuaweiRegisterMap.BatteryBlocks, optional: true, cancellationToken));

        return HuaweiRegisterMap.Decode(registers, identity, _deviceId, DateTimeOffset.UtcNow);
    }

    private static void Merge(Dictionary<ushort, ushort> into, Dictionary<ushort, ushort> from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }

    /// <param name="optional">Return what was read so far when the inverter refuses an address (Modbus exception 0x02).</param>
    private async Task<Dictionary<ushort, ushort>> ReadBlocksAsync(
        IEnumerable<(ushort Start, ushort Count)> blocks, bool optional, CancellationToken cancellationToken)
    {
        var registers = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in blocks)
        {
            for (var offset = 0; offset < count; offset += HuaweiRegisterMap.MaxRegistersPerRead)
            {
                var n = (ushort)Math.Min(HuaweiRegisterMap.MaxRegistersPerRead, count - offset);
                var at = (ushort)(start + offset);
                ushort[] values;
                try
                {
                    values = await _client.ReadRegistersAsync(_options.UnitId, at, n, ModbusTcpClient.ReadHoldingRegisters, cancellationToken);
                }
                catch (ModbusException ex) when (optional && ex.ExceptionCode == 0x02)
                {
                    return registers;
                }
                for (var i = 0; i < values.Length; i++) registers[(ushort)(at + i)] = values[i];
            }
        }
        return registers;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

using System.Text;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Modbus;

namespace SolPnl.Plugin.Sungrow;

public sealed record SungrowOptions
{
    /// <summary>WiNet-S dongle or inverter LAN port: IP address or host name.</summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;

    /// <summary>WiNet-S dongles can be slow to answer.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Reads a Sungrow SG string inverter or SH hybrid over Modbus TCP. The device type is read on the first poll and
/// picks the family's registers.
/// </summary>
public sealed class SungrowPlugin : IDevicePlugin, IDeviceState, IAsyncDisposable
{
    private readonly SungrowOptions _options;
    private readonly ModbusTcpClient _client;
    private readonly string _deviceId;
    private Identity? _identity;

    private sealed record Identity(ushort DeviceType, SungrowFamily Family, string? Serial, double? NominalKw);

    public SungrowPlugin(SungrowOptions options)
    {
        _options = options;
        _client = new ModbusTcpClient(options.Host, options.Port, options.Timeout);
        _deviceId = DeviceKey(options.Host);
    }

    /// <summary>"sungrow-192-168-1-80" for host 192.168.1.80: from the settings, letters, digits and dashes only.</summary>
    public static string DeviceKey(string host)
    {
        var sb = new StringBuilder("sungrow-");
        foreach (var c in host.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    public string Id => SungrowPluginType.PluginId;
    public string DisplayName => "Sungrow inverter (Modbus TCP)";

    public string StateMetric => SungrowRegisterMap.StateMetric;

    public string? DescribeState(int value) => SungrowCodes.DescribeState(value);

    /// <summary>Doesn't talk to the inverter; the model and serial number come from the first read.</summary>
    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        var model = _identity is { } id ? SungrowCodes.DescribeModel(id.DeviceType) : "Inverter";
        return Task.FromResult(new DeviceInfo(_deviceId, "Sungrow", model, _identity?.Serial, FirmwareVersion: null));
    }

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var identity = _identity ??= await ReadIdentityAsync(cancellationToken);
        var registers = await ReadBlocksAsync(SungrowRegisterMap.Blocks(identity.Family), cancellationToken);
        return SungrowRegisterMap.Decode(registers, identity.Family, _deviceId, DateTimeOffset.UtcNow);
    }

    private async Task<Identity> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        var registers = await ReadBlocksAsync(SungrowRegisterMap.IdentityBlocks, cancellationToken);
        var type = registers[5000];
        if (type is 0 or 0xFFFF)
            throw new InvalidDataException("The inverter didn't report its device type (register 5000). Is this a Sungrow inverter?");
        return new Identity(type, SungrowRegisterMap.FamilyOf(type), SungrowRegisterMap.DecodeSerial(registers),
            registers[5001] is var p and not 0xFFFF ? p * 0.1 : null);
    }

    /// <summary>Reads blocks by documented register number; the result is keyed the same way.</summary>
    private async Task<Dictionary<ushort, ushort>> ReadBlocksAsync(
        IEnumerable<(ushort Start, ushort Count)> blocks, CancellationToken cancellationToken)
    {
        var registers = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in blocks)
        {
            var values = await _client.ReadRegistersAsync(_options.UnitId, SungrowRegisterMap.ToWireAddress(start), count,
                ModbusTcpClient.ReadInputRegisters, cancellationToken);
            for (var i = 0; i < values.Length; i++) registers[(ushort)(start + i)] = values[i];
        }
        return registers;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

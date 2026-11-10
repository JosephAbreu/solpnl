using System.Text;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Modbus;

namespace SolPnl.Plugin.VictronGx;

public sealed record VictronGxOptions
{
    /// <summary>GX device IP address or host name on the LAN.</summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 502;

    /// <summary>The VE.Bus inverter/charger's unit ID; null to find it among the usual ones.</summary>
    public byte? VebusUnitId { get; init; }

    public IReadOnlyList<byte> SolarChargerUnitIds { get; init; } = [];

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Reads a Victron system through its GX device: the system overview (power flows and battery), the VE.Bus
/// inverter/charger (AC input and output, state, errors) and optionally each solar charger.
/// </summary>
public sealed class VictronGxPlugin : IDevicePlugin, IDeviceFaults, IDeviceState, IAsyncDisposable
{
    private readonly VictronGxOptions _options;
    private readonly ModbusTcpClient _client;
    private readonly string _deviceId;
    private string? _serial;
    private bool _serialRead;

    /// <summary>The VE.Bus unit in use; 0 = looked and found none (until the next failure).</summary>
    private byte? _vebusUnit;

    public VictronGxPlugin(VictronGxOptions options)
    {
        _options = options;
        _client = new ModbusTcpClient(options.Host, options.Port, options.Timeout);
        _deviceId = DeviceKey(options.Host);
        _vebusUnit = options.VebusUnitId;
    }

    /// <summary>"victron-192-168-1-70" for host 192.168.1.70: from the settings, letters, digits and dashes only.</summary>
    public static string DeviceKey(string host)
    {
        var sb = new StringBuilder("victron-");
        foreach (var c in host.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    public string Id => VictronGxPluginType.PluginId;
    public string DisplayName => "Victron GX device (Modbus TCP)";

    public string? DescribeFault(int code) => VictronCodes.Describe(code);

    public string StateMetric => VictronRegisterMap.StateMetric;

    public string? DescribeState(int value) => VictronCodes.DescribeState(value);

    /// <summary>Doesn't talk to the device; the GX serial number comes from the first read.</summary>
    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInfo(_deviceId, "Victron Energy", "GX device", _serial, FirmwareVersion: null));

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var system = await ReadBlocksAsync(VictronRegisterMap.SystemUnitId, VictronRegisterMap.SystemBlocks, tolerant: true, cancellationToken);
            if (system.Count == 0)
                throw new InvalidDataException(
                    $"The GX device didn't return any system values on unit ID {VictronRegisterMap.SystemUnitId}. Check that Modbus TCP is on.");

            if (!_serialRead)
            {
                var (start, count) = VictronRegisterMap.SystemSerialBlock;
                try { _serial = VictronRegisterMap.DecodeSerial(await Read(VictronRegisterMap.SystemUnitId, start, count, cancellationToken)); }
                catch (ModbusException) { }
                _serialRead = true;
            }

            var vebus = await ReadVebusAsync(cancellationToken);

            var chargers = new List<IReadOnlyDictionary<ushort, ushort>>();
            foreach (var unit in _options.SolarChargerUnitIds)
                chargers.Add(await ReadBlocksAsync(unit, VictronRegisterMap.SolarChargerBlocks, tolerant: false, cancellationToken));

            return VictronRegisterMap.Decode(new VictronRegisterMap.Inputs(system, vebus, chargers), _deviceId, DateTimeOffset.UtcNow);
        }
        catch
        {
            if (_options.VebusUnitId is null) _vebusUnit = null; // look again next time
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<ushort, ushort>?> ReadVebusAsync(CancellationToken cancellationToken)
    {
        if (_vebusUnit is { } known)
        {
            if (known == 0) return null;
            return await ReadBlocksAsync(known, VictronRegisterMap.VebusBlocks, tolerant: false, cancellationToken);
        }

        foreach (var candidate in VictronRegisterMap.VebusUnitCandidates)
        {
            try
            {
                var values = await ReadBlocksAsync(candidate, VictronRegisterMap.VebusBlocks, tolerant: false, cancellationToken);
                _vebusUnit = candidate;
                return values;
            }
            catch (ModbusException)
            {
                // no VE.Bus device on this unit ID
            }
        }
        _vebusUnit = 0;
        return null;
    }

    /// <param name="tolerant">Skip blocks the device refuses (a value it doesn't have) instead of failing.</param>
    private async Task<Dictionary<ushort, ushort>> ReadBlocksAsync(
        byte unit, IEnumerable<(ushort Start, ushort Count)> blocks, bool tolerant, CancellationToken cancellationToken)
    {
        var registers = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in blocks)
        {
            ushort[] values;
            try
            {
                values = await Read(unit, start, count, cancellationToken);
            }
            catch (ModbusException) when (tolerant)
            {
                continue;
            }
            for (var i = 0; i < values.Length; i++) registers[(ushort)(start + i)] = values[i];
        }
        return registers;
    }

    private Task<ushort[]> Read(byte unit, ushort start, ushort count, CancellationToken cancellationToken) =>
        _client.ReadRegistersAsync(unit, start, count, ModbusTcpClient.ReadHoldingRegisters, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

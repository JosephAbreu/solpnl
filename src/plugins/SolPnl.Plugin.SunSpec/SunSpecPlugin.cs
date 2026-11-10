using System.Text;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Modbus;

namespace SolPnl.Plugin.SunSpec;

public sealed record SunSpecOptions
{
    /// <summary>Inverter (or data manager) IP address or host name on the LAN.</summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;

    /// <summary>A grid meter with its own Modbus address (e.g. a Fronius Smart Meter), if any.</summary>
    public byte? MeterUnitId { get; init; }

    /// <summary>The meter reports power sold to the grid as positive.</summary>
    public bool MeterPositiveIsExport { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Reads a SunSpec inverter, and a SunSpec grid meter if there is one, over Modbus TCP. The device's model list is
/// discovered on the first read and again after any failure (e.g. after a firmware update moved things).
/// </summary>
public sealed class SunSpecPlugin : IDevicePlugin, IDeviceFaults, IDeviceState, IAsyncDisposable
{
    private readonly SunSpecOptions _options;
    private readonly ModbusTcpClient _client;
    private readonly string _deviceId;
    private Layout? _layout;

    private sealed record Layout(SunSpecIdentity? Identity, SunSpecModel? Inverter, SunSpecModel? Mppt,
        (byte Unit, SunSpecModel Model)? Meter);

    public SunSpecPlugin(SunSpecOptions options)
    {
        _options = options;
        _client = new ModbusTcpClient(options.Host, options.Port, options.Timeout);
        _deviceId = DeviceKey(options.Host, options.UnitId);
    }

    /// <summary>
    /// Built from the settings so it's known without talking to the device: "sunspec-192-168-1-60-1" for host
    /// 192.168.1.60, unit 1. Only letters, digits and dashes, so it's safe in URLs.
    /// </summary>
    public static string DeviceKey(string host, byte unitId)
    {
        var sb = new StringBuilder("sunspec-");
        foreach (var c in host.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.Append('-').Append(unitId).ToString();
    }

    public string Id => SunSpecPluginType.PluginId;
    public string DisplayName => "SunSpec inverter (Modbus TCP)";

    public string? DescribeFault(int code) => SunSpecEvents.Describe(code);

    public string StateMetric => SunSpecDecoder.StateMetric;

    public string? DescribeState(int value) => SunSpecStates.Describe(value);

    /// <summary>Doesn't talk to the device; the manufacturer, model and serial number come from the first read.</summary>
    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        var id = _layout?.Identity;
        return Task.FromResult(new DeviceInfo(_deviceId, id?.Manufacturer ?? "SunSpec", id?.Model ?? "Inverter",
            id?.SerialNumber, FirmwareVersion: id?.Version));
    }

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var layout = _layout ??= await DiscoverAsync(cancellationToken);
            var readings = new SunSpecDecoder.Readings(_deviceId, DateTimeOffset.UtcNow);

            if (layout.Inverter is { } inverter)
                SunSpecDecoder.DecodeInverter(inverter.Id, await ReadModelAsync(_options.UnitId, inverter, cancellationToken), readings);
            if (layout.Mppt is { } mppt)
                SunSpecDecoder.DecodeMppt(await ReadModelAsync(_options.UnitId, mppt, cancellationToken), readings);
            if (layout.Meter is { } meter)
            {
                SunSpecDecoder.DecodeMeter(meter.Model.Id, await ReadModelAsync(meter.Unit, meter.Model, cancellationToken),
                    _options.MeterPositiveIsExport, readings);
                SunSpecDecoder.AddLoad(readings);
            }

            return readings.List;
        }
        catch
        {
            _layout = null; // rediscover next time
            throw;
        }
    }

    private async Task<Layout> DiscoverAsync(CancellationToken cancellationToken)
    {
        var models = await SunSpecDiscovery.DiscoverAsync(Reader(_options.UnitId), cancellationToken);

        var common = models.FirstOrDefault(m => m.Id == SunSpecDecoder.CommonModel);
        var identity = common is null ? null : SunSpecDecoder.DecodeCommon(await ReadModelAsync(_options.UnitId, common, cancellationToken));
        var inverter = models.FirstOrDefault(m => SunSpecDecoder.IsInverter(m.Id));
        var mppt = models.FirstOrDefault(m => m.Id == SunSpecDecoder.MpptModel);

        (byte, SunSpecModel)? meter = models.FirstOrDefault(m => SunSpecDecoder.IsMeter(m.Id)) is { } own ? (_options.UnitId, own) : null;
        if (meter is null && _options.MeterUnitId is { } meterUnit)
        {
            var meterModels = await SunSpecDiscovery.DiscoverAsync(Reader(meterUnit), cancellationToken);
            meter = meterModels.FirstOrDefault(m => SunSpecDecoder.IsMeter(m.Id)) is { } found
                ? (meterUnit, found)
                : throw new InvalidDataException($"No SunSpec meter found at Modbus address {meterUnit}.");
        }

        if (inverter is null && meter is null)
            throw new InvalidDataException(
                $"The device speaks SunSpec but has no inverter or meter model (found models {string.Join(", ", models.Select(m => m.Id))}).");

        return new Layout(identity, inverter, mppt, meter);
    }

    private SunSpecDiscovery.ReadRegisters Reader(byte unit) =>
        (start, count, ct) => _client.ReadRegistersAsync(unit, start, count, ModbusTcpClient.ReadHoldingRegisters, ct);

    private Task<ushort[]> ReadModelAsync(byte unit, SunSpecModel model, CancellationToken cancellationToken) =>
        SunSpecDiscovery.ReadModelAsync(Reader(unit), model, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

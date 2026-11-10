using System.Globalization;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.SunSpec;

/// <summary>Device type: any inverter (and optional grid meter) that speaks SunSpec over Modbus TCP on the LAN.</summary>
public sealed class SunSpecPluginType : IDevicePluginType
{
    public const string PluginId = "sunspec";

    public const string PositiveIsImport = "Positive means buying from the grid";
    public const string PositiveIsExport = "Positive means selling to the grid";

    public string Id => PluginId;
    public string DisplayName => "SunSpec inverter (Modbus TCP)";

    public string Description =>
        "Inverters that support SunSpec over Modbus TCP, such as Fronius, SMA, SolarEdge and KACO, plus a SunSpec grid " +
        "meter if you have one, read directly over your network (no cloud). Turn on Modbus TCP in the inverter first.";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "Inverter address", Placeholder: "192.168.1.60",
            Help: "IP address or host name of the inverter (or of its data manager, e.g. a Fronius Datamanager)."),
        new("port", "Port", SettingKind.Number, Required: false, Default: "502",
            Help: "The Modbus TCP port. 502 unless you changed it (SolarEdge uses 1502 on some firmware)."),
        new("unitId", "Modbus address", SettingKind.Number, Required: false, Default: "1",
            Help: "The inverter's Modbus address (unit ID): 1 for Fronius and SolarEdge, 3 for SMA. Shown next to the " +
                  "Modbus TCP setting in the inverter's setup."),
        new("meterUnitId", "Meter Modbus address", SettingKind.Number, Required: false,
            Help: "Only if your grid meter has its own Modbus address, e.g. 200 or 240 for a Fronius Smart Meter. Leave " +
                  "empty when there's no meter, or when the meter is listed with the inverter (as SolarEdge does)."),
        new("meterDirection", "Meter direction", SettingKind.Choice, Required: false, Default: PositiveIsImport,
            Options: [PositiveIsImport, PositiveIsExport],
            Help: "How your meter reports grid power. If the Grid reading shows selling at night, switch this."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) => new SunSpecPlugin(ParseOptions(settings));

    public static SunSpecOptions ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var host = Value(settings, "host");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Inverter address is required.");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' isn't a valid IP address or host name.");

        var port = ParseOptional(settings, "port", 502, 1, 65535, "Port");
        var unit = ParseOptional(settings, "unitId", 1, 0, 255, "Modbus address");
        var meterUnit = string.IsNullOrEmpty(Value(settings, "meterUnitId"))
            ? (byte?)null
            : (byte)ParseOptional(settings, "meterUnitId", 0, 0, 255, "Meter Modbus address");
        if (meterUnit == unit) meterUnit = null; // same unit: the meter is found in the inverter's own model list

        var positiveIsExport = Value(settings, "meterDirection") switch
        {
            null or "" or PositiveIsImport => false,
            PositiveIsExport => true,
            _ => throw new ArgumentException($"Meter direction must be \"{PositiveIsImport}\" or \"{PositiveIsExport}\"."),
        };

        return new SunSpecOptions
        {
            Host = host, Port = port, UnitId = (byte)unit, MeterUnitId = meterUnit, MeterPositiveIsExport = positiveIsExport,
        };
    }

    private static string? Value(IReadOnlyDictionary<string, string?> settings, string key) =>
        settings.TryGetValue(key, out var v) ? v?.Trim() : null;

    private static int ParseOptional(IReadOnlyDictionary<string, string?> settings, string key, int fallback, int min, int max, string label)
    {
        var raw = Value(settings, key);
        if (string.IsNullOrEmpty(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ArgumentException($"{label} must be a whole number from {min} to {max}.");
        return value;
    }
}

using System.Globalization;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.VictronGx;

/// <summary>Device type: a Victron system read through its GX device (Cerbo GX, Venus GX, ...) over Modbus TCP.</summary>
public sealed class VictronGxPluginType : IDevicePluginType
{
    public const string PluginId = "victron-gx";

    public string Id => PluginId;
    public string DisplayName => "Victron GX device (Modbus TCP)";

    public string Description =>
        "Victron systems (MultiPlus, Quattro, SmartSolar and BlueSolar chargers, batteries) through their GX device, " +
        "such as a Cerbo GX, read directly over your network (no cloud). Turn on Modbus TCP on the GX first " +
        "(Settings → Services → Modbus TCP).";

    /// <summary>Each SmartSolar/BlueSolar charger is one PV input.</summary>
    public string PvInputName => "charger";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "GX device address", Placeholder: "192.168.1.70",
            Help: "IP address or host name of the Cerbo GX, Venus GX or other GX device."),
        new("port", "Port", SettingKind.Number, Required: false, Default: "502",
            Help: "The Modbus TCP port. Always 502 on a GX device."),
        new("vebusUnitId", "Inverter/charger unit ID", SettingKind.Number, Required: false,
            Help: "Unit ID of the MultiPlus or Quattro, listed on the GX under Settings → Services → Modbus TCP → " +
                  "Available services. Leave empty to find it automatically (227 on a Cerbo GX)."),
        new("solarChargerUnitIds", "Solar charger unit IDs", Required: false, Placeholder: "226, 229",
            Help: "Unit IDs of your SmartSolar/BlueSolar chargers, separated by commas, from the same list. Optional: " +
                  "total solar power is read either way; these add per-charger values."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) => new VictronGxPlugin(ParseOptions(settings));

    public static VictronGxOptions ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var host = Value(settings, "host");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("GX device address is required.");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' isn't a valid IP address or host name.");

        var port = ParseUnit(Value(settings, "port"), 1, 65535, "Port") ?? 502;
        var vebus = (byte?)ParseUnit(Value(settings, "vebusUnitId"), 0, 255, "Inverter/charger unit ID");

        var chargers = new List<byte>();
        foreach (var part in (Value(settings, "solarChargerUnitIds") ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var unit = (byte)ParseUnit(part, 0, 255, "Solar charger unit IDs")!;
            if (!chargers.Contains(unit)) chargers.Add(unit);
        }

        return new VictronGxOptions { Host = host, Port = port, VebusUnitId = vebus, SolarChargerUnitIds = chargers };
    }

    private static string? Value(IReadOnlyDictionary<string, string?> settings, string key) =>
        settings.TryGetValue(key, out var v) ? v?.Trim() : null;

    private static int? ParseUnit(string? raw, int min, int max, string label)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ArgumentException($"{label} must be whole numbers from {min} to {max}.");
        return value;
    }
}

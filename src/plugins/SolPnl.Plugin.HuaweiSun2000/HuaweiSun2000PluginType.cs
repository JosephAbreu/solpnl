using System.Globalization;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.HuaweiSun2000;

/// <summary>Device type: a Huawei SUN2000 inverter (with optional meter and LUNA2000 battery) over Modbus TCP.</summary>
public sealed class HuaweiSun2000PluginType : IDevicePluginType
{
    public const string PluginId = "huawei-sun2000";

    public string Id => PluginId;
    public string DisplayName => "Huawei SUN2000 inverter (Modbus TCP)";

    public string Description =>
        "Huawei SUN2000 inverters, with a Huawei power meter and LUNA2000 battery if you have them, through the " +
        "SDongle or the inverter's own WiFi, read directly over your network (no cloud). Turn on Modbus TCP first.";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "Inverter address", Placeholder: "192.168.1.90",
            Help: "IP address of the SDongle, or 192.168.200.1 when connected to the inverter's own WiFi."),
        new("port", "Port", SettingKind.Number, Required: false, Default: "502",
            Help: "502 for the SDongle; 6607 for the inverter's own WiFi on most firmware."),
        new("unitId", "Modbus address", SettingKind.Number, Required: false, Default: "1",
            Help: "The inverter's Modbus address: usually 1 through the SDongle and 0 on the inverter's own WiFi. With " +
                  "several inverters on one SDongle, each has its own (add one device per inverter)."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) => new HuaweiSun2000Plugin(ParseOptions(settings));

    public static HuaweiSun2000Options ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var host = Value(settings, "host");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Inverter address is required.");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' isn't a valid IP address or host name.");

        var port = ParseOptional(settings, "port", 502, 1, 65535, "Port");
        var unit = ParseOptional(settings, "unitId", 1, 0, 247, "Modbus address");
        return new HuaweiSun2000Options { Host = host, Port = port, UnitId = (byte)unit };
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

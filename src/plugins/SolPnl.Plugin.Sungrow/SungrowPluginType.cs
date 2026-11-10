using System.Globalization;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Sungrow;

/// <summary>Device type: a Sungrow SG string inverter or SH hybrid read over Modbus TCP on the LAN.</summary>
public sealed class SungrowPluginType : IDevicePluginType
{
    public const string PluginId = "sungrow";

    public string Id => PluginId;
    public string DisplayName => "Sungrow inverter (Modbus TCP)";

    public string Description =>
        "Sungrow SG string inverters and SH hybrids through the WiNet-S dongle or the inverter's LAN port, read " +
        "directly over your network (no cloud). The model and family are detected automatically.";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "Inverter address", Placeholder: "192.168.1.80",
            Help: "IP address of the WiNet-S dongle or of the inverter's LAN port. The LAN port is more reliable when " +
                  "the inverter has one."),
        new("port", "Port", SettingKind.Number, Required: false, Default: "502",
            Help: "The Modbus TCP port. Always 502."),
        new("unitId", "Modbus address", SettingKind.Number, Required: false, Default: "1",
            Help: "The inverter's Modbus address. 1 unless you changed it."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) => new SungrowPlugin(ParseOptions(settings));

    public static SungrowOptions ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var host = Value(settings, "host");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Inverter address is required.");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' isn't a valid IP address or host name.");

        var port = ParseOptional(settings, "port", 502, 1, 65535, "Port");
        var unit = ParseOptional(settings, "unitId", 1, 0, 247, "Modbus address");
        return new SungrowOptions { Host = host, Port = port, UnitId = (byte)unit };
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

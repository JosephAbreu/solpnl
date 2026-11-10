using System.Globalization;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Luxpower;

/// <summary>Device type: an EG4 / Luxpower hybrid inverter read through its WiFi dongle on the LAN.</summary>
public sealed class LuxpowerPluginType : IDevicePluginType
{
    public const string PluginId = "luxpower";

    public string Id => PluginId;
    public string DisplayName => "EG4 / Luxpower inverter";

    public string Description =>
        "EG4 hybrid inverters (18kPV, 12kPV, 6000XP, ...) and other Luxpower models, through the inverter's WiFi " +
        "dongle, read directly over your network (no cloud).";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "Dongle address", Placeholder: "192.168.1.95",
            Help: "IP address of the inverter's WiFi dongle. Find it in your router's list of devices."),
        new("datalogSerial", "Dongle serial number", Placeholder: "BA12345678",
            Help: "The 10-character serial number on the WiFi dongle's sticker (also shown as the datalog in the EG4 or " +
                  "Luxpower app)."),
        new("inverterSerial", "Inverter serial number", Placeholder: "4512345678",
            Help: "The inverter's 10-character serial number, on its label or in the app."),
        new("port", "Port", SettingKind.Number, Required: false, Default: "8000",
            Help: "The dongle's local port. 8000 unless you changed it."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) => new LuxpowerPlugin(ParseOptions(settings));

    public static LuxpowerOptions ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var host = Value(settings, "host");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Dongle address is required.");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' isn't a valid IP address or host name.");

        var datalog = Serial(settings, "datalogSerial", "Dongle serial number");
        var inverter = Serial(settings, "inverterSerial", "Inverter serial number");

        var raw = Value(settings, "port");
        var port = 8000;
        if (!string.IsNullOrEmpty(raw) &&
            (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new ArgumentException("Port must be a whole number from 1 to 65535.");

        return new LuxpowerOptions { Host = host, Port = port, DatalogSerial = datalog, InverterSerial = inverter };
    }

    private static string Serial(IReadOnlyDictionary<string, string?> settings, string key, string label)
    {
        var value = Value(settings, key)?.ToUpperInvariant();
        if (value is not { Length: 10 } || !value.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException($"{label} must be exactly 10 letters and digits.");
        return value;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> settings, string key) =>
        settings.TryGetValue(key, out var v) ? v?.Trim() : null;
}

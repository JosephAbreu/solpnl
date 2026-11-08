using System.Globalization;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.SolarmanSrne;

/// <summary>Device type: an SRNE-family inverter read through a Solarman V5 WiFi stick logger on the LAN.</summary>
public sealed class SolarmanSrnePluginType : IDevicePluginType
{
    public const string PluginId = "solarman-srne";

    public string Id => PluginId;
    public string DisplayName => "Solarman logger + SRNE inverter";

    public string Description =>
        "SRNE-family hybrid inverters (e.g. Sungoldpower split-phase) through a Solarman WiFi stick logger such as " +
        "the LSW-5, read directly over your network (no cloud).";

    /// <summary>SRNE's display and manuals call each PV input a "line".</summary>
    public string PvInputName => "line";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "Logger address", Placeholder: "192.168.1.50",
            Help: "IP address or host name of the WiFi stick logger. Find it in your router's list of devices."),
        new("loggerSerial", "Logger serial number", SettingKind.Number, Placeholder: "2712345678",
            Help: "The 10-digit serial number on the logger's sticker (not the inverter's)."),
        new("port", "Port", SettingKind.Number, Required: false, Default: "8899",
            Help: "The logger's local Modbus port. Almost always 8899."),
        new("slaveId", "Modbus address", SettingKind.Number, Required: false, Default: "1",
            Help: "The inverter's Modbus address. 1 unless you changed it on the inverter."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) =>
        new SolarmanSrnePlugin(ParseOptions(settings));

    public static SolarmanSrneOptions ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var host = Value(settings, "host");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Logger address is required.");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' isn't a valid IP address or host name.");

        if (!uint.TryParse(Value(settings, "loggerSerial"), NumberStyles.None, CultureInfo.InvariantCulture, out var serial) || serial == 0)
            throw new ArgumentException("Logger serial number must be a positive whole number (the digits on the logger's sticker).");

        var port = ParseOptional(settings, "port", 8899, 1, 65535, "Port");
        var slave = ParseOptional(settings, "slaveId", 1, 1, 247, "Modbus address");

        return new SolarmanSrneOptions { Host = host, LoggerSerial = serial, Port = port, SlaveId = (byte)slave };
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

using System.Globalization;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Fronius;

/// <summary>Device type: a Fronius inverter (with its meter and battery) read through the Fronius Solar API on the LAN.</summary>
public sealed class FroniusPluginType(HttpClient http) : IDevicePluginType
{
    public const string PluginId = "fronius";

    public string Id => PluginId;
    public string DisplayName => "Fronius inverter (Solar API)";

    public string Description =>
        "Fronius inverters (Symo, Primo, Galvo, Eco, GEN24, Tauro) with their Smart Meter and battery, through the " +
        "Fronius Solar API, read directly over your network (no cloud). Nothing to turn on: the API is on by default.";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "Inverter address", Placeholder: "192.168.1.60",
            Help: "IP address or host name of the inverter (or its Datamanager). Opening it in a browser shows the " +
                  "Fronius web interface."),
        new("deviceId", "Inverter number", SettingKind.Number, Required: false, Default: "1",
            Help: "Only with several inverters on one Datamanager: the inverter's number in the Fronius web " +
                  "interface. Add one device per inverter."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) => new FroniusPlugin(ParseOptions(settings), http);

    public static FroniusOptions ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var raw = (settings.TryGetValue("host", out var h) ? h?.Trim() : null) ?? "";
        if (raw.Length == 0) throw new ArgumentException("Inverter address is required.");

        // Accept "192.168.1.60", "inverter.local:8080" or a pasted "http://192.168.1.60/".
        var text = raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.HostNameType == UriHostNameType.Unknown || raw.Any(char.IsWhiteSpace))
            throw new ArgumentException($"'{raw}' isn't a valid IP address or host name.");

        var deviceRaw = settings.TryGetValue("deviceId", out var d) ? d?.Trim() : null;
        var deviceId = 1;
        if (!string.IsNullOrEmpty(deviceRaw) &&
            (!int.TryParse(deviceRaw, NumberStyles.None, CultureInfo.InvariantCulture, out deviceId) || deviceId is < 0 or > 255))
            throw new ArgumentException("Inverter number must be a whole number from 0 to 255.");

        return new FroniusOptions { BaseUri = new Uri(uri.GetLeftPart(UriPartial.Authority)), DeviceId = deviceId };
    }
}

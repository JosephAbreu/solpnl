using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Enphase;

/// <summary>Device type: an Enphase IQ Gateway (Envoy) read through its local API.</summary>
public sealed class EnphasePluginType : IDevicePluginType
{
    public const string PluginId = "enphase";

    public string Id => PluginId;
    public string DisplayName => "Enphase IQ Gateway";

    public string Description =>
        "Enphase microinverter systems through the IQ Gateway (Envoy), with its CT meters and IQ Batteries if you " +
        "have them, read directly over your network. Gateways on firmware 7 or later need an access token from " +
        "Enphase, renewed once a year.";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("host", "Gateway address", Placeholder: "192.168.1.65",
            Help: "IP address or host name of the IQ Gateway (envoy.local often works)."),
        new("token", "Access token", SettingKind.Secret, Required: false,
            Help: "For firmware 7 and later: sign in at entrez.enphaseenergy.com with your Enphase account, choose " +
                  "your system and gateway, and paste the token here. Owner tokens last a year; SolPnl shows the days " +
                  "left. Leave empty for older firmware."),
        new("certFingerprint", "Certificate fingerprint", Required: false, Placeholder: "AB:CD:EF:…",
            Help: "Optional. The SHA-256 fingerprint of the gateway's HTTPS certificate (in your browser: open the " +
                  "gateway's address, click the padlock or warning, view the certificate). If set, SolPnl accepts only " +
                  "that certificate; if empty, it trusts the certificate it first sees and accepts only that one after."),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings) => new EnphasePlugin(ParseOptions(settings));

    public static EnphaseOptions ParseOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var host = (settings.TryGetValue("host", out var h) ? h?.Trim() : null) ?? "";
        if (host.Length == 0) throw new ArgumentException("Gateway address is required.");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' isn't a valid IP address or host name.");

        var token = settings.TryGetValue("token", out var t) ? t?.Trim() : null;
        if (string.IsNullOrEmpty(token)) token = null;
        else if (token.Count(c => c == '.') != 2 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("That doesn't look like an Enphase access token (it should be one long line with two dots).");

        var raw = settings.TryGetValue("certFingerprint", out var f) ? f?.Trim() : null;
        string? fingerprint = null;
        if (!string.IsNullOrEmpty(raw))
        {
            fingerprint = new string(raw.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
            if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
                throw new ArgumentException("Certificate fingerprint must be a SHA-256 fingerprint: 64 hex digits (colons optional).");
        }

        return new EnphaseOptions { Host = host, Token = token, CertificateFingerprint = fingerprint };
    }
}

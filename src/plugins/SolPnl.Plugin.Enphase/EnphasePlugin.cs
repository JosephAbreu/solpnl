using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Enphase;

public sealed record EnphaseOptions
{
    /// <summary>IQ Gateway IP address or host name on the LAN.</summary>
    public required string Host { get; init; }

    /// <summary>JWT access token from Enphase (firmware 7+); null for older firmware.</summary>
    public string? Token { get; init; }

    /// <summary>SHA-256 fingerprint (hex, upper case, no separators) the gateway's certificate must have; null to pin on first use.</summary>
    public string? CertificateFingerprint { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Reads an Enphase IQ Gateway through its local API: production and consumption (production.json), CT meter
/// voltages, and IQ Battery charge and power.
/// </summary>
/// <remarks>
/// The gateway serves HTTPS with a self-signed certificate. It's pinned: either to the fingerprint in the settings,
/// or to the certificate seen on the first connection (until SolPnl restarts or the device's settings change). The
/// access token is only ever sent over HTTPS, and a certificate problem never falls back to HTTP; only a gateway
/// that refuses HTTPS altogether (older firmware) is read over HTTP, without the token.
/// </remarks>
public sealed class EnphasePlugin : IDevicePlugin, IAsyncDisposable
{
    private readonly EnphaseOptions _options;
    private readonly HttpClient _http;
    private readonly string _deviceId;
    private readonly DateTimeOffset? _tokenExpiry;
    private string? _pinned;
    private string? _rejected;
    private Uri? _base;
    private EnphaseInfo? _info;
    private bool _hasMeters = true, _hasBatteries = true;

    /// <param name="handler">For tests; normally one that pins the gateway's certificate.</param>
    public EnphasePlugin(EnphaseOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _pinned = options.CertificateFingerprint;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, errors) => Validate(cert, errors) },
        }, disposeHandler: true) { Timeout = options.Timeout };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _tokenExpiry = options.Token is { } t ? EnphaseDecoder.TokenExpiry(t) : null;
        _deviceId = DeviceKey(options.Host);
    }

    /// <summary>The fingerprint of the certificate currently trusted (pinned or configured), if any.</summary>
    public string? TrustedFingerprint => _pinned;

    /// <summary>Accepts a certificate the system trusts, or one matching the pinned fingerprint (pinning the first one seen).</summary>
    public bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null) return false;
        var fingerprint = Fingerprint(certificate);
        var pinned = Interlocked.CompareExchange(ref _pinned, fingerprint, null) ?? fingerprint;
        if (pinned == fingerprint) return true;
        _rejected = fingerprint;
        return false;
    }

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>"enphase-192-168-1-65": from the settings, letters, digits and dashes only.</summary>
    public static string DeviceKey(string host)
    {
        var sb = new StringBuilder("enphase-");
        foreach (var c in host.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    public string Id => EnphasePluginType.PluginId;
    public string DisplayName => "Enphase IQ Gateway";

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInfo(_deviceId, "Enphase", _info?.PartNumber is { } pn ? $"IQ Gateway ({pn})" : "IQ Gateway",
            _info?.SerialNumber, _info?.Firmware));

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_base is null) await DiscoverAsync(cancellationToken);
            var readings = new EnphaseDecoder.Readings(_deviceId, DateTimeOffset.UtcNow);

            using (var production = await GetJsonAsync("production.json?details=1", cancellationToken))
                EnphaseDecoder.DecodeProduction(production!.RootElement, readings);

            if (_hasMeters && _info?.Metered != false)
            {
                using var meters = await GetJsonAsync("ivp/meters", cancellationToken, optional: true);
                using var values = meters is null ? null : await GetJsonAsync("ivp/meters/readings", cancellationToken, optional: true);
                if (meters is null || values is null) _hasMeters = false;
                else EnphaseDecoder.DecodeMeters(meters.RootElement, values.RootElement, readings);
            }

            if (_hasBatteries)
            {
                using var secctrl = await GetJsonAsync("ivp/ensemble/secctrl", cancellationToken, optional: true);
                if (secctrl is null) _hasBatteries = false;
                else
                {
                    using var power = await GetJsonAsync("ivp/ensemble/power", cancellationToken, optional: true);
                    EnphaseDecoder.DecodeBatteries(secctrl.RootElement, power?.RootElement, readings);
                }
            }

            EnphaseDecoder.AddTokenDaysLeft(_tokenExpiry, readings);
            return readings.List;
        }
        catch (HttpRequestException ex) when (IsCertificateRejection(ex) && _rejected is { } now)
        {
            throw new HttpRequestException(
                $"The gateway's HTTPS certificate isn't the one SolPnl trusts (it presented {Format(now)}, expected " +
                $"{Format(_pinned!)}). If you replaced the gateway, restart SolPnl or update the certificate fingerprint " +
                "in this device's settings; otherwise something else may be answering at this address.", ex);
        }
    }

    private static bool IsCertificateRejection(Exception ex) =>
        ex.InnerException is AuthenticationException || ex.InnerException?.InnerException is AuthenticationException;

    private static string Format(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));

    /// <summary>/info (no authentication) over HTTPS; HTTP only when the gateway refuses HTTPS connections outright.</summary>
    private async Task DiscoverAsync(CancellationToken ct)
    {
        foreach (var scheme in new[] { "https", "http" })
        {
            var candidate = new Uri($"{scheme}://{_options.Host}/");
            try
            {
                using var response = await _http.GetAsync(new Uri(candidate, "info"), ct);
                response.EnsureSuccessStatusCode();
                _info = EnphaseDecoder.ParseInfo(await response.Content.ReadAsStringAsync(ct));
                _base = candidate;
                return;
            }
            catch (HttpRequestException ex) when (scheme == "https" && ex.InnerException is SocketException
                                                  { SocketErrorCode: SocketError.ConnectionRefused })
            {
                // Older firmware has no HTTPS. (Certificate failures don't land here: they never fall back to HTTP.)
            }
        }
    }

    /// <param name="optional">Return null if the gateway doesn't have this (404) instead of failing.</param>
    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct, bool optional = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base!, path));
        if (_options.Token is { } token && _base!.Scheme == Uri.UriSchemeHttps)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); // never over plain HTTP

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (optional && path.StartsWith("ivp/ensemble", StringComparison.Ordinal)) return null; // no batteries
            throw new HttpRequestException(_options.Token is null
                ? "The gateway needs an access token (firmware 7 or later). Get one at entrez.enphaseenergy.com and add it to this device's settings."
                : _tokenExpiry is { } exp && exp < DateTimeOffset.UtcNow
                    ? $"The Enphase access token expired on {exp:yyyy-MM-dd}. Get a new one at entrez.enphaseenergy.com and update this device's settings."
                    : "The gateway rejected the access token. Check that it was made for this gateway's serial number.",
                null, response.StatusCode);
        }
        if (optional && response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

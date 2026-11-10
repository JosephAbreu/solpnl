using System.Net;
using System.Text;
using System.Text.Json;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Fronius;

public sealed record FroniusOptions
{
    /// <summary>The inverter's (or Datamanager's) web address, e.g. http://192.168.1.60.</summary>
    public required Uri BaseUri { get; init; }

    /// <summary>The inverter's number on the Datamanager (1 for a single inverter).</summary>
    public int DeviceId { get; init; } = 1;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Reads a Fronius inverter through the Fronius Solar API v1: the power flow (solar, grid, load, battery), the
/// inverter's own values, and the Smart Meter and battery when present. The inverter's values are missing while it
/// sleeps at night; the Datamanager and meter keep answering, so the device stays online.
/// </summary>
public sealed class FroniusPlugin : IDevicePlugin, IDeviceState, IAsyncDisposable
{
    private readonly FroniusOptions _options;
    private readonly HttpClient _http;
    private readonly string _deviceId;
    private Uri? _apiBase;
    private bool? _hasThreePhaseData;
    private bool _hasMeter = true, _hasStorage = true;
    private int? _meterPhases;

    public FroniusPlugin(FroniusOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
        _deviceId = DeviceKey(options.BaseUri, options.DeviceId);
    }

    /// <summary>"fronius-192-168-1-60-1": from the settings, letters, digits and dashes only.</summary>
    public static string DeviceKey(Uri baseUri, int deviceId)
    {
        var sb = new StringBuilder("fronius-");
        foreach (var c in baseUri.Authority.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.Append('-').Append(deviceId).ToString();
    }

    public string Id => FroniusPluginType.PluginId;
    public string DisplayName => "Fronius inverter (Solar API)";

    public string StateMetric => FroniusDecoder.StateMetric;

    public string? DescribeState(int value) => FroniusStates.Describe(value);

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInfo(_deviceId, "Fronius", "Inverter", SerialNumber: null, FirmwareVersion: null));

    public async Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var api = _apiBase ??= await DiscoverAsync(cancellationToken);
        var readings = new FroniusDecoder.Readings(_deviceId, DateTimeOffset.UtcNow);
        var id = _options.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The power flow always answers (it's the Datamanager's own view); a failure here fails the poll.
        using (var flow = await GetAsync(api, "GetPowerFlowRealtimeData.fcgi", cancellationToken))
            FroniusDecoder.DecodePowerFlow(FroniusDecoder.Data(flow), id, readings);

        await InverterAsync(api, id, readings, cancellationToken);

        if (_hasMeter)
            _hasMeter = await OptionalAsync(api, "GetMeterRealtimeData.cgi?Scope=System",
                data => _meterPhases = FroniusDecoder.DecodeMeter(data, _meterPhases, readings), cancellationToken);
        if (_hasStorage)
            _hasStorage = await OptionalAsync(api, "GetStorageRealtimeData.cgi?Scope=System",
                data => FroniusDecoder.DecodeStorage(data, readings), cancellationToken);

        FroniusDecoder.AddBatteryCurrent(readings);
        return readings.List;
    }

    private async Task InverterAsync(Uri api, string id, FroniusDecoder.Readings readings, CancellationToken ct)
    {
        JsonElement common;
        using var commonDoc = await GetAsync(api, $"GetInverterRealtimeData.cgi?Scope=Device&DeviceId={id}&DataCollection=CommonInverterData", ct);
        try
        {
            common = FroniusDecoder.Data(commonDoc);
        }
        catch (FroniusDecoder.StatusException ex) when (ex.Code == FroniusDecoder.DeviceNotAvailable)
        {
            return; // asleep at night
        }

        JsonDocument? threeDoc = null;
        try
        {
            JsonElement? three = null;
            if (_hasThreePhaseData != false)
            {
                try
                {
                    threeDoc = await GetAsync(api, $"GetInverterRealtimeData.cgi?Scope=Device&DeviceId={id}&DataCollection=3PInverterData", ct);
                    three = FroniusDecoder.Data(threeDoc);
                    _hasThreePhaseData = FroniusDecoder.Value(three.Value, "UAC_L1") is not null || _hasThreePhaseData == true;
                }
                catch (Exception ex) when (ex is FroniusDecoder.StatusException or HttpRequestException or JsonException or KeyNotFoundException)
                {
                    _hasThreePhaseData ??= false; // single-phase inverter
                }
            }
            FroniusDecoder.DecodeInverter(common, three, readings);
        }
        finally
        {
            threeDoc?.Dispose();
        }
    }

    /// <summary>Reads an endpoint that only answers when the equipment is there. Returns false if it isn't.</summary>
    private async Task<bool> OptionalAsync(Uri api, string path, Action<JsonElement> decode, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync(api, path, ct);
            var data = FroniusDecoder.Data(doc);
            if (data.ValueKind != JsonValueKind.Object || !data.EnumerateObject().Any()) return false;
            decode(data);
            return true;
        }
        catch (FroniusDecoder.StatusException ex) when (ex.Code != FroniusDecoder.DeviceNotAvailable)
        {
            return false; // e.g. 255 "Storages are not supported"
        }
        catch (FroniusDecoder.StatusException)
        {
            return true; // there, but not answering right now
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>GetAPIVersion.cgi → BaseURL (e.g. "/solar_api/v1/").</summary>
    private async Task<Uri> DiscoverAsync(CancellationToken ct)
    {
        using var doc = await GetAsync(new Uri(_options.BaseUri, "/solar_api/"), "GetAPIVersion.cgi", ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("APIVersion", out var version) || version.GetInt32() < 1 ||
            !root.TryGetProperty("BaseURL", out var baseUrl) || baseUrl.GetString() is not { } path)
            throw new InvalidDataException("This Fronius device only offers Solar API version 0, which SolPnl doesn't support. A firmware update adds version 1.");
        return new Uri(_options.BaseUri, path.EndsWith('/') ? path : path + "/");
    }

    private async Task<JsonDocument> GetAsync(Uri api, string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.Timeout);
        using var response = await _http.GetAsync(new Uri(api, path), cts.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // the HttpClient is SolPnl's shared one
}

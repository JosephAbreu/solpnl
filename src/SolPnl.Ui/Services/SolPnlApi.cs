using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolPnl.Contracts.Models;

namespace SolPnl.Ui.Services;

/// <summary>Typed client for the SolPnl REST API (/api/v1). Shared by the web PWA and future native shells.</summary>
public sealed class SolPnlApi(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<SetupStatus> GetSetupStatusAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SetupStatus>("api/v1/setup/status", Json, ct) ?? new SetupStatus(false);

    public async Task<SetupDefaults?> GetSetupDefaultsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SetupDefaults>("api/v1/setup/defaults", Json, ct);

    public Task<SetupResult> TestStorageAsync(StorageSetupRequest request, CancellationToken ct = default) =>
        PostSetupAsync("api/v1/setup/storage/test", request, ct);

    public Task<SetupResult> SaveStorageAsync(StorageSetupRequest request, CancellationToken ct = default) =>
        PostSetupAsync("api/v1/setup/storage", request, ct);

    /// <summary>Setup endpoints answer errors (wrong code, missing field) with a <see cref="SetupResult"/> body too.</summary>
    private async Task<SetupResult> PostSetupAsync(string url, StorageSetupRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, Json, ct);
        try
        {
            var result = await response.Content.ReadFromJsonAsync<SetupResult>(Json, ct);
            if (result is not null) return result;
        }
        catch (JsonException)
        {
        }
        return new SetupResult(false, $"SolPnl answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DeviceInfo>>("api/v1/devices", Json, ct) ?? [];

    public async Task<IReadOnlyList<Reading>> GetLatestAsync(string? deviceId = null, CancellationToken ct = default)
    {
        var url = deviceId is null ? "api/v1/readings/latest" : $"api/v1/readings/latest?device={Uri.EscapeDataString(deviceId)}";
        return await http.GetFromJsonAsync<List<Reading>>(url, Json, ct) ?? [];
    }

    public async Task<HistoryResult> GetHistoryAsync(
        string deviceId, string metric, DateTimeOffset from, DateTimeOffset to,
        HistoryResolution? resolution = null, CancellationToken ct = default)
    {
        var url = "api/v1/readings/history" +
                  $"?device={Uri.EscapeDataString(deviceId)}" +
                  $"&metric={Uri.EscapeDataString(metric)}" +
                  $"&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
                  $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
                  (resolution is { } r ? $"&resolution={r.ToString().ToLowerInvariant()}" : "");
        return await http.GetFromJsonAsync<HistoryResult>(url, Json, ct)
               ?? new HistoryResult(deviceId, metric, null, resolution ?? HistoryResolution.Raw, []);
    }
}

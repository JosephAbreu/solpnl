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

    // ---- Site: settings, overview, daily summaries -----------------------------------------------------------

    public async Task<SiteSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SiteSettings>("api/v1/settings", Json, ct) ?? new SiteSettings(null, TemperatureUnits.Fahrenheit);

    public async Task<ApiResult<SiteLocation>> SetLocationAsync(SiteLocation location, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("api/v1/settings/location", location, Json, ct);
        return await ReadResultAsync<SiteLocation>(response, ct);
    }

    public async Task<ApiResult<bool>> SetTemperatureUnitAsync(string unit, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("api/v1/settings/units", new SetUnitsRequest(unit), Json, ct);
        if (response.IsSuccessStatusCode) return new ApiResult<bool>(true, null);
        return new ApiResult<bool>(false, (await ReadResultAsync<object>(response, ct)).Error);
    }

    public async Task<ApiResult<List<LocationSearchResult>>> SearchLocationsAsync(string query, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/settings/location/search?q={Uri.EscapeDataString(query)}", ct);
        return await ReadResultAsync<List<LocationSearchResult>>(response, ct);
    }

    public async Task<SiteOverview?> GetSiteOverviewAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SiteOverview>("api/v1/site/overview", Json, ct);

    public async Task<IReadOnlyList<DaySummary>> GetDaySummariesAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (from is { } f) query.Add($"from={f:yyyy-MM-dd}");
        if (to is { } t) query.Add($"to={t:yyyy-MM-dd}");
        var url = "api/v1/site/days" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return await http.GetFromJsonAsync<List<DaySummary>>(url, Json, ct) ?? [];
    }

    // ---- Plugin settings and notifications -------------------------------------------------------------------

    public async Task<PluginSettingsInfo?> GetPluginSettingsAsync(string pluginId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<PluginSettingsInfo>($"api/v1/plugins/{Uri.EscapeDataString(pluginId)}/settings", Json, ct);

    public async Task<ApiResult<PluginSettingsInfo>> SavePluginSettingsAsync(string pluginId, Dictionary<string, string?> values, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/plugins/{Uri.EscapeDataString(pluginId)}/settings",
            new SavePluginSettingsRequest(values), Json, ct);
        return await ReadResultAsync<PluginSettingsInfo>(response, ct);
    }

    public async Task<PluginActionResult> TestPluginSettingsAsync(string pluginId, Dictionary<string, string?> values, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/plugins/{Uri.EscapeDataString(pluginId)}/settings/test",
            new SavePluginSettingsRequest(values), Json, ct);
        var result = await ReadResultAsync<PluginActionResult>(response, ct);
        return result.Value ?? new PluginActionResult(false, result.Error ?? "The test failed.");
    }

    public async Task<NotificationPreferences> GetNotificationPreferencesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<NotificationPreferences>("api/v1/notifications/preferences", Json, ct) ?? new();

    public async Task<ApiResult<NotificationPreferences>> SetNotificationPreferencesAsync(NotificationPreferences preferences, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("api/v1/notifications/preferences", preferences, Json, ct);
        return await ReadResultAsync<NotificationPreferences>(response, ct);
    }

    public async Task<NotificationTestResult> SendTestNotificationAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("api/v1/notifications/test", content: null, ct);
        var result = await ReadResultAsync<NotificationTestResult>(response, ct);
        return result.Value ?? new NotificationTestResult(false, [new PluginActionResult(false, result.Error ?? "The test failed.")]);
    }

    public async Task<AlertsPage> GetAlertsAsync(string? category = null, long? before = null, int limit = 50, CancellationToken ct = default)
    {
        var query = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrEmpty(category)) query.Add($"category={Uri.EscapeDataString(category)}");
        if (before is { } b) query.Add($"before={b}");
        return await http.GetFromJsonAsync<AlertsPage>("api/v1/alerts?" + string.Join("&", query), Json, ct) ?? new AlertsPage([], [], false);
    }

    // ---- Plugins and device configuration --------------------------------------------------------------------

    public async Task<IReadOnlyList<PluginInfo>> GetPluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PluginInfo>>("api/v1/plugins", Json, ct) ?? [];

    public async Task<ApiResult<PluginInfo>> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/plugins/{Uri.EscapeDataString(pluginId)}",
            new SetPluginEnabledRequest(enabled), Json, ct);
        return await ReadResultAsync<PluginInfo>(response, ct);
    }

    public async Task<IReadOnlyList<ConfiguredDevice>> GetConfiguredDevicesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ConfiguredDevice>>("api/v1/config/devices", Json, ct) ?? [];

    public async Task<DeviceTestResult> TestDeviceAsync(AddDeviceRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/config/devices/test", request, Json, ct);
        var result = await ReadResultAsync<DeviceTestResult>(response, ct);
        return result.Value ?? new DeviceTestResult(false, result.Error ?? "The test failed.");
    }

    public async Task<ApiResult<ConfiguredDevice>> AddDeviceAsync(AddDeviceRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/config/devices", request, Json, ct);
        return await ReadResultAsync<ConfiguredDevice>(response, ct);
    }

    public async Task<ApiResult<bool>> RemoveDeviceAsync(int id, bool deleteHistory, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/v1/config/devices/{id}?deleteHistory={(deleteHistory ? "true" : "false")}", ct);
        if (response.IsSuccessStatusCode) return new ApiResult<bool>(true, null);
        return new ApiResult<bool>(false, (await ReadResultAsync<object>(response, ct)).Error);
    }

    /// <summary>Deletes the stored history of a device that is no longer configured.</summary>
    public async Task<ApiResult<bool>> DeleteDeviceHistoryAsync(string deviceId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/v1/devices/{Uri.EscapeDataString(deviceId)}", ct);
        if (response.IsSuccessStatusCode) return new ApiResult<bool>(true, null);
        return new ApiResult<bool>(false, (await ReadResultAsync<object>(response, ct)).Error);
    }

    /// <summary>Success body as <typeparamref name="T"/>, or the server's <see cref="ApiError"/> message.</summary>
    private static async Task<ApiResult<T>> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (response.IsSuccessStatusCode)
                return new ApiResult<T>(await response.Content.ReadFromJsonAsync<T>(Json, ct), null);

            var error = await response.Content.ReadFromJsonAsync<ApiError>(Json, ct);
            return new ApiResult<T>(default, error?.Error ?? $"SolPnl answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (JsonException)
        {
            return new ApiResult<T>(default, $"SolPnl answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
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

/// <summary>Either a value or a user-facing error message from SolPnl.</summary>
public sealed record ApiResult<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
}

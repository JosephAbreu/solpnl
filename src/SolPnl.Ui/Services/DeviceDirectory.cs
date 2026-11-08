using SolPnl.Contracts.Models;

namespace SolPnl.Ui.Services;

/// <summary>
/// One device as the UI shows it: configured devices (with or without data yet) plus devices that only have stored
/// history (removed from configuration but history kept).
/// </summary>
public sealed record DeviceRow(
    string DeviceId,
    string Name,
    ConfiguredDevice? Config,
    DeviceInfo? Info,
    PluginInfo? Plugin,
    DateTimeOffset? LastReading)
{
    public bool IsConfigured => Config is not null;

    public DeviceState State(DateTimeOffset now) =>
        DeviceStatus.For(IsConfigured, Plugin?.Enabled ?? false, LastReading, now);

    public string PluginName => Plugin?.DisplayName ?? Config?.PluginId ?? "–";
}

public static class DeviceDirectory
{
    public static async Task<List<DeviceRow>> LoadAsync(SolPnlApi api, CancellationToken ct = default)
    {
        var pluginsTask = api.GetPluginsAsync(ct);
        var configsTask = api.GetConfiguredDevicesAsync(ct);
        var reportedTask = api.GetDevicesAsync(ct);
        var latestTask = api.GetLatestAsync(null, ct);
        await Task.WhenAll(pluginsTask, configsTask, reportedTask, latestTask);

        var plugins = pluginsTask.Result.ToDictionary(p => p.Id);
        // Weather is logged under a reserved device key; it isn't hardware, so it's not listed as a device.
        var reported = reportedTask.Result.Where(d => d.DeviceId != SiteDevices.Weather).ToDictionary(d => d.DeviceId);
        var last = latestTask.Result.GroupBy(r => r.DeviceId).ToDictionary(g => g.Key, g => g.Max(r => r.Timestamp));

        var rows = configsTask.Result.Select(c => new DeviceRow(
            c.DeviceId, c.Name, c, reported.GetValueOrDefault(c.DeviceId), plugins.GetValueOrDefault(c.PluginId),
            last.TryGetValue(c.DeviceId, out var t) ? t : null)).ToList();

        var configuredIds = rows.Select(r => r.DeviceId).ToHashSet();
        rows.AddRange(reported.Values.Where(d => !configuredIds.Contains(d.DeviceId)).Select(d => new DeviceRow(
            d.DeviceId, d.DeviceId, null, d, null, last.TryGetValue(d.DeviceId, out var t) ? t : null)));

        return rows.OrderBy(r => r.IsConfigured ? 0 : 1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

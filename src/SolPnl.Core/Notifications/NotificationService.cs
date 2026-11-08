using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Data;
using SolPnl.Core.Devices;
using SolPnl.Core.Site;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Notifications;

/// <summary>
/// Decides what to notify about and sends it through every enabled, configured notification plugin:
/// new weather alerts, devices going offline (and coming back), and solar underperformance warnings.
/// Each event is recorded in notification_log so it's sent once, even across restarts.
/// </summary>
public sealed class NotificationService(
    IDbContextFactory<SolPnlDb> dbFactory,
    PluginCatalog plugins,
    PluginSettingsStore pluginSettings,
    DeviceRegistry devices,
    IReadingStore store,
    SiteService site,
    TimeProvider time,
    ILogger<NotificationService> logger)
{
    private const string PreferencesKey = "notifications.preferences";
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InsightCheckInterval = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new();
    private DateTimeOffset _nextInsightCheck = DateTimeOffset.MinValue;

    // ---- Preferences ---------------------------------------------------------------------------------------

    public async Task<NotificationPreferences> GetPreferencesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.AppSettings.AsNoTracking().Where(s => s.Key == PreferencesKey).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return json is null ? new NotificationPreferences() : JsonSerializer.Deserialize<NotificationPreferences>(json) ?? new();
    }

    public async Task<NotificationPreferences> SetPreferencesAsync(NotificationPreferences preferences, CancellationToken ct = default)
    {
        if (preferences.OfflineMinutes is < 2 or > 1440)
            throw new ArgumentException("Offline alert delay must be between 2 and 1,440 minutes.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == PreferencesKey, ct);
        var json = JsonSerializer.Serialize(preferences);
        if (row is null) db.AppSettings.Add(new AppSettingEntity { Key = PreferencesKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
        return preferences;
    }

    // ---- Sending -------------------------------------------------------------------------------------------

    /// <summary>Sends a test notification through every enabled, configured notification plugin.</summary>
    public async Task<NotificationTestResult> SendTestAsync(CancellationToken ct = default)
    {
        var channels = await ChannelsAsync(ct);
        if (channels.Count == 0)
            return new NotificationTestResult(false,
                [new PluginActionResult(false, "No notification plugin is enabled and configured. Set one up below, e.g. Email.")]);

        var alert = new Alert("test", AlertSeverity.Info, "Test notification",
            "This is a test notification from SolPnl. If you can read this, notifications are working.", time.GetUtcNow());
        var results = new List<PluginActionResult>();
        foreach (var (plugin, settings) in channels)
        {
            try
            {
                await plugin.SendAsync(alert, settings, ct);
                results.Add(new PluginActionResult(true, $"{plugin.DisplayName}: sent."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new PluginActionResult(false, $"{plugin.DisplayName}: {ex.GetBaseException().Message}"));
            }
        }
        return new NotificationTestResult(results.All(r => r.Ok), results);
    }

    /// <summary>Checks all event sources once and sends whatever is new.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var channels = await ChannelsAsync(ct);
        if (channels.Count == 0) return; // nowhere to send; events are picked up once a channel is configured

        var prefs = await GetPreferencesAsync(ct);
        var now = time.GetUtcNow();

        if (prefs.WeatherAlerts)
        {
            foreach (var alert in site.Alerts)
            {
                await RaiseOnceAsync($"weather:{alert.Id}", channels, new Alert(alert.Id, Severity(alert.Severity), alert.Event,
                    WeatherMessage(alert), now), ct);
            }
        }

        if (prefs.DeviceOffline)
        {
            foreach (var device in devices.ActiveDevices)
            {
                var latest = await store.GetLatestAsync(device.DeviceKey, ct);
                if (latest.Count == 0) continue; // never reported yet: "waiting", not offline
                var last = latest.Max(r => r.Timestamp);
                var key = $"offline:{device.DeviceKey}";
                if (now - last > TimeSpan.FromMinutes(prefs.OfflineMinutes))
                {
                    await RaiseOnceAsync(key, channels, new Alert(key, AlertSeverity.Critical, $"{device.Name} is offline",
                        $"SolPnl hasn't received data from {device.Name} since {Local(last)} ({(int)(now - last).TotalMinutes} minutes). " +
                        "Check that the device and its network connection are powered and working.", now, device.DeviceKey), ct);
                }
                else
                {
                    await ClearAsync(key, channels, new Alert(key, AlertSeverity.Info, $"{device.Name} is back online",
                        $"SolPnl is receiving data from {device.Name} again (latest {Local(last)}).", now, device.DeviceKey), ct);
                }
            }
        }

        if (prefs.SolarUnderperformance && now >= _nextInsightCheck)
        {
            _nextInsightCheck = now + InsightCheckInterval;
            var overview = await site.GetOverviewAsync(ct);
            const string key = "insight:underperformance";
            var warning = overview.Insights.FirstOrDefault(i => i.Kind == InsightKinds.Underperformance && i.Level == InsightLevels.Warning);
            if (warning is not null)
                await RaiseOnceAsync(key, channels, new Alert(key, AlertSeverity.Warning, warning.Title, warning.Message, now), ct);
            else
                await ClearAsync(key, channels, notifyOnClear: null, ct);
        }
    }

    /// <summary>Sends <paramref name="alert"/> unless this event is already open; then marks it open.</summary>
    private async Task RaiseOnceAsync(string key, IReadOnlyList<(INotificationPlugin, IReadOnlyDictionary<string, string?>)> channels,
        Alert alert, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.NotificationLog.SingleOrDefaultAsync(n => n.EventKey == key, ct);
        if (row is { ClearedMs: null }) return; // already notified and still open
        if (!await SendAsync(key, channels, alert, ct)) return;

        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (row is null) db.NotificationLog.Add(new NotificationLogEntity { EventKey = key, RaisedMs = nowMs });
        else
        {
            row.RaisedMs = nowMs;
            row.ClearedMs = null;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Closes an open event, optionally sending a "resolved" notification.</summary>
    private async Task ClearAsync(string key, IReadOnlyList<(INotificationPlugin, IReadOnlyDictionary<string, string?>)> channels,
        Alert? notifyOnClear, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.NotificationLog.SingleOrDefaultAsync(n => n.EventKey == key, ct);
        if (row is not { ClearedMs: null }) return;
        if (notifyOnClear is not null && !await SendAsync(key + ":cleared", channels, notifyOnClear, ct)) return;
        row.ClearedMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends to every channel; true if at least one accepted it. Failed events are retried after a pause.</summary>
    private async Task<bool> SendAsync(string key, IReadOnlyList<(INotificationPlugin Plugin, IReadOnlyDictionary<string, string?> Settings)> channels,
        Alert alert, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_retryAfter.TryGetValue(key, out var retry) && now < retry) return false;

        var delivered = false;
        foreach (var (plugin, settings) in channels)
        {
            try
            {
                await plugin.SendAsync(alert, settings, ct);
                delivered = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Sending '{Title}' via {Plugin} failed: {Message}", alert.Title, plugin.DisplayName, ex.GetBaseException().Message);
            }
        }

        if (delivered)
        {
            _retryAfter.Remove(key);
            logger.LogInformation("Notification sent: {Title}", alert.Title);
        }
        else
        {
            _retryAfter[key] = now + RetryAfterFailure;
        }
        return delivered;
    }

    /// <summary>Enabled notification plugins with all required settings, and their effective settings.</summary>
    private async Task<List<(INotificationPlugin Plugin, IReadOnlyDictionary<string, string?> Settings)>> ChannelsAsync(CancellationToken ct)
    {
        var channels = new List<(INotificationPlugin, IReadOnlyDictionary<string, string?>)>();
        foreach (var plugin in await plugins.NotificationPluginsAsync(ct))
        {
            if (plugin is IConfigurablePlugin configurable)
            {
                var settings = await pluginSettings.GetEffectiveAsync(configurable, ct);
                if (!PluginSettingsStore.IsConfigured(configurable.PluginSettings, settings)) continue;
                channels.Add((plugin, settings));
            }
            else
            {
                channels.Add((plugin, new Dictionary<string, string?>()));
            }
        }
        return channels;
    }

    private static AlertSeverity Severity(string nwsSeverity) => nwsSeverity switch
    {
        "Extreme" or "Severe" => AlertSeverity.Critical,
        "Moderate" => AlertSeverity.Warning,
        _ => AlertSeverity.Info,
    };

    private static string WeatherMessage(WeatherAlert alert)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(alert.Headline)) parts.Add(alert.Headline);
        if (alert.Expires is { } until) parts.Add($"Until {Local(until)}.");
        if (!string.IsNullOrWhiteSpace(alert.Description)) parts.Add(Trim(alert.Description, 1500));
        if (!string.IsNullOrWhiteSpace(alert.Instruction)) parts.Add(Trim(alert.Instruction, 800));
        if (!string.IsNullOrWhiteSpace(alert.Source)) parts.Add($"Source: {alert.Source}");
        return string.Join("\n\n", parts);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text.Trim() : text[..max].TrimEnd() + "…";

    private static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

/// <summary>Runs <see cref="NotificationService.RunOnceAsync"/> every minute.</summary>
public sealed class NotificationBackgroundService(NotificationService notifications, ILogger<NotificationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give polling and weather a moment after startup so "offline" isn't judged on stale data.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await notifications.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Checking for notifications failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

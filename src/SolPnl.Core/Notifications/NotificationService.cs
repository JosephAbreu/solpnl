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
/// Raises alerts and delivers them through every enabled, configured notification plugin:
/// new weather alerts, devices going offline (and coming back), and solar underperformance warnings.
///
/// Raising and delivering are separate:
///  - Raising always happens (whether or not a channel is set up). notification_log tracks which conditions are
///    open so each is raised once, even across restarts; alert_history keeps every alert for the Alerts page.
///  - Delivering sends undelivered alerts from the last 24 hours whenever a channel is available, retrying failures
///    after a pause. Alerts that couldn't be delivered within 24 hours are given up on (shown as "not sent").
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
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(365);
    private static readonly TimeSpan InsightCheckInterval = TimeSpan.FromMinutes(15);

    private readonly Dictionary<long, DateTimeOffset> _retryAfter = new();
    private DateTimeOffset _nextInsightCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

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

    // ---- History -------------------------------------------------------------------------------------------

    /// <summary>Open conditions and alert history, newest first; page with <paramref name="beforeId"/>.</summary>
    public async Task<AlertsPage> GetAlertsAsync(string? category = null, int limit = 50, long? beforeId = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.AlertHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(category)) query = query.Where(a => a.Category == category);
        if (beforeId is { } before) query = query.Where(a => a.Id < before);
        var rows = await query.OrderByDescending(a => a.Id).Take(limit + 1).ToListAsync(ct);

        // Open conditions, described by the alert that opened them.
        var open = await db.NotificationLog.AsNoTracking().Where(n => n.ClearedMs == null).ToListAsync(ct);
        var openKeys = open.Select(o => o.EventKey).ToList();
        var openers = await db.AlertHistory.AsNoTracking().Where(a => openKeys.Contains(a.EventKey)).ToListAsync(ct);
        var active = open
            .Select(o => (Log: o, Entry: openers.Where(a => a.EventKey == o.EventKey).MaxBy(a => a.Id)))
            .Where(x => x.Entry is not null)
            .Where(x => string.IsNullOrEmpty(category) || x.Entry!.Category == category)
            .OrderByDescending(x => x.Log.RaisedMs)
            .Select(x => new ActiveAlert(x.Entry!.Category, x.Entry.Severity, x.Entry.Title,
                DateTimeOffset.FromUnixTimeMilliseconds(x.Log.RaisedMs), x.Entry.Id))
            .ToList();

        return new AlertsPage(active, rows.Take(limit).Select(r => ToDto(r, now)).ToList(), rows.Count > limit);
    }

    public static AlertEntry ToDto(AlertHistoryEntity r, DateTimeOffset now)
    {
        var raised = DateTimeOffset.FromUnixTimeMilliseconds(r.RaisedMs);
        var status = r.DeliveredMs is not null ? AlertDeliveryStatus.Sent
            : now - raised > DeliveryWindow ? AlertDeliveryStatus.NotSent
            : r.Attempts > 0 ? AlertDeliveryStatus.Failed
            : AlertDeliveryStatus.Waiting;
        return new AlertEntry(r.Id, r.Category, r.Severity, r.Title, r.Message, r.DeviceId, raised,
            r.DeliveredMs is { } d ? DateTimeOffset.FromUnixTimeMilliseconds(d) : null, status, r.Delivery);
    }

    // ---- Raising -------------------------------------------------------------------------------------------

    /// <summary>Raises new alerts from all sources, then delivers anything undelivered.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var prefs = await GetPreferencesAsync(ct);
        var now = time.GetUtcNow();

        if (prefs.WeatherAlerts)
        {
            foreach (var alert in site.Alerts)
            {
                await RaiseAsync($"weather:{alert.Id}", AlertCategories.Weather,
                    new Alert(alert.Id, Severity(alert.Severity), alert.Event, WeatherMessage(alert), now), ct);
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
                    await RaiseAsync(key, AlertCategories.Device, new Alert(key, AlertSeverity.Critical, $"{device.Name} is offline",
                        $"SolPnl hasn't received data from {device.Name} since {Local(last)} ({(int)(now - last).TotalMinutes} minutes). " +
                        "Check that the device and its network connection are powered and working.", now, device.DeviceKey), ct);
                }
                else
                {
                    await ClearAsync(key, AlertCategories.Device, new Alert(key, AlertSeverity.Info, $"{device.Name} is back online",
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
                await RaiseAsync(key, AlertCategories.Solar, new Alert(key, AlertSeverity.Warning, warning.Title, warning.Message, now), ct);
            else
                await ClearAsync(key, AlertCategories.Solar, recovery: null, ct);
        }

        await DeliverPendingAsync(ct);

        if (now >= _nextCleanup)
        {
            _nextCleanup = now.AddDays(1);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var cutoff = (now - HistoryRetention).ToUnixTimeMilliseconds();
            await db.Database.ExecuteSqlAsync($"delete from alert_history where raised_ms < {cutoff}", ct);
        }
    }

    /// <summary>Records an alert unless its condition is already open; then marks the condition open.</summary>
    private async Task RaiseAsync(string key, string category, Alert alert, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.NotificationLog.SingleOrDefaultAsync(n => n.EventKey == key, ct);
        if (row is { ClearedMs: null }) return; // already raised and still going on

        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (row is null) db.NotificationLog.Add(new NotificationLogEntity { EventKey = key, RaisedMs = nowMs });
        else
        {
            row.RaisedMs = nowMs;
            row.ClearedMs = null;
        }
        db.AlertHistory.Add(NewEntry(key, category, alert));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Alert raised: {Title}", alert.Title);
    }

    /// <summary>Closes an open condition, optionally recording a "resolved" alert.</summary>
    private async Task ClearAsync(string key, string category, Alert? recovery, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.NotificationLog.SingleOrDefaultAsync(n => n.EventKey == key, ct);
        if (row is not { ClearedMs: null }) return;

        row.ClearedMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (recovery is not null) db.AlertHistory.Add(NewEntry(key + ":cleared", category, recovery));
        await db.SaveChangesAsync(ct);
    }

    private static AlertHistoryEntity NewEntry(string key, string category, Alert alert) => new()
    {
        EventKey = key,
        Category = category,
        Severity = alert.Severity.ToString().ToLowerInvariant(),
        Title = alert.Title.Length > 300 ? alert.Title[..300] : alert.Title,
        Message = alert.Message,
        DeviceId = alert.DeviceId,
        RaisedMs = alert.RaisedAt.ToUnixTimeMilliseconds(),
        Attempts = 0,
    };

    // ---- Delivering ----------------------------------------------------------------------------------------

    /// <summary>Sends undelivered alerts from the last 24 hours, oldest first, if any channel is set up.</summary>
    private async Task DeliverPendingAsync(CancellationToken ct)
    {
        var channels = await ChannelsAsync(ct);
        if (channels.Count == 0) return; // they wait (and show as "waiting") until a channel is set up

        var now = time.GetUtcNow();
        var since = (now - DeliveryWindow).ToUnixTimeMilliseconds();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.AlertHistory
            .Where(a => a.DeliveredMs == null && a.RaisedMs >= since && a.Category != AlertCategories.Test)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        foreach (var entry in pending)
        {
            if (_retryAfter.TryGetValue(entry.Id, out var retry) && now < retry) continue;

            var alert = new Alert(entry.EventKey, ParseSeverity(entry.Severity), entry.Title, entry.Message,
                DateTimeOffset.FromUnixTimeMilliseconds(entry.RaisedMs), entry.DeviceId);
            var (delivered, report) = await SendToAllAsync(channels, alert, ct);

            entry.Attempts++;
            entry.Delivery = report;
            if (delivered)
            {
                entry.DeliveredMs = time.GetUtcNow().ToUnixTimeMilliseconds();
                _retryAfter.Remove(entry.Id);
                logger.LogInformation("Notification sent: {Title}", entry.Title);
            }
            else
            {
                _retryAfter[entry.Id] = now + RetryAfterFailure;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Sends a test notification through every channel and records it in the history.</summary>
    public async Task<NotificationTestResult> SendTestAsync(CancellationToken ct = default)
    {
        var channels = await ChannelsAsync(ct);
        if (channels.Count == 0)
            return new NotificationTestResult(false,
                [new PluginActionResult(false, "No notification plugin is enabled and configured. Set one up below, e.g. Email.")]);

        var now = time.GetUtcNow();
        var alert = new Alert("test", AlertSeverity.Info, "Test notification",
            "This is a test notification from SolPnl. If you can read this, notifications are working.", now);

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

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = NewEntry($"test:{now.ToUnixTimeMilliseconds()}", AlertCategories.Test, alert);
        entry.Attempts = 1;
        entry.Delivery = string.Join("\n", results.Select(r => r.Message));
        if (results.Any(r => r.Ok)) entry.DeliveredMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        db.AlertHistory.Add(entry);
        await db.SaveChangesAsync(ct);

        return new NotificationTestResult(results.All(r => r.Ok), results);
    }

    /// <summary>Sends to every channel; true if at least one accepted it, plus a per-channel report.</summary>
    private async Task<(bool Delivered, string Report)> SendToAllAsync(
        IReadOnlyList<(INotificationPlugin Plugin, IReadOnlyDictionary<string, string?> Settings)> channels, Alert alert, CancellationToken ct)
    {
        var lines = new List<string>();
        var delivered = false;
        foreach (var (plugin, settings) in channels)
        {
            try
            {
                await plugin.SendAsync(alert, settings, ct);
                delivered = true;
                lines.Add($"{plugin.DisplayName}: sent");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = ex.GetBaseException().Message;
                lines.Add($"{plugin.DisplayName}: failed ({message})");
                logger.LogWarning("Sending '{Title}' via {Plugin} failed: {Message}", alert.Title, plugin.DisplayName, message);
            }
        }
        return (delivered, string.Join("\n", lines));
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

    private static AlertSeverity ParseSeverity(string severity) =>
        Enum.TryParse<AlertSeverity>(severity, ignoreCase: true, out var s) ? s : AlertSeverity.Info;

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

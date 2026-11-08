using SolPnl.Contracts.Plugins;

namespace SolPnl.Contracts.Models;

public static class PluginKinds
{
    public const string Device = "device";
    public const string Notification = "notification";
    /// <summary>Weather, alerts and other data services (not hardware).</summary>
    public const string Service = "service";
}

/// <summary>An installed plugin and whether it's enabled. Disabled device plugins don't poll any of their devices.</summary>
/// <param name="Settings">For device plugins: the settings each device has.</param>
/// <param name="HasSettings">The plugin itself has settings (a card on the Settings page), e.g. an email server.</param>
public sealed record PluginInfo(
    string Id,
    string DisplayName,
    string Description,
    string Kind,
    bool Enabled,
    int DeviceCount,
    IReadOnlyList<SettingField> Settings,
    bool HasSettings = false);

/// <summary>
/// A plugin's own settings for the Settings page. Secret values are never returned: <see cref="SecretsSet"/> lists
/// which secrets have a saved value.
/// </summary>
public sealed record PluginSettingsInfo(
    string PluginId,
    string DisplayName,
    IReadOnlyList<SettingField> Fields,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<string> SecretsSet,
    bool IsConfigured,
    string? TestLabel);

/// <summary>
/// Settings to save or test. For secret fields: null (or missing) keeps the saved value, "" clears it, anything else
/// replaces it. Other fields: empty removes the value.
/// </summary>
public sealed record SavePluginSettingsRequest(Dictionary<string, string?> Values);

public sealed record PluginActionResult(bool Ok, string Message);

/// <summary>Which events send notifications (through every enabled, configured notification plugin).</summary>
public sealed record NotificationPreferences(
    bool WeatherAlerts = true,
    bool DeviceOffline = true,
    int OfflineMinutes = 10,
    bool SolarUnderperformance = true);

public sealed record NotificationTestResult(bool Ok, IReadOnlyList<PluginActionResult> Results);

public sealed record SetPluginEnabledRequest(bool Enabled);

/// <summary>
/// A device the user has configured. <see cref="DeviceId"/> is the key its readings are stored under (from the plugin).
/// Secret settings are returned as null.
/// </summary>
public sealed record ConfiguredDevice(
    int Id,
    string Name,
    string PluginId,
    string DeviceId,
    IReadOnlyDictionary<string, string?> Settings,
    DateTimeOffset CreatedAt);

public sealed record AddDeviceRequest(string Name, string PluginId, Dictionary<string, string?> Settings);

/// <summary>Result of trying a device's settings: connects and takes one reading.</summary>
public sealed record DeviceTestResult(bool Ok, string Message, string? DeviceId = null, int ReadingCount = 0);

/// <summary>Error body for rejected management requests.</summary>
public sealed record ApiError(string Error);

public static class AlertCategories
{
    public const string Weather = "weather";
    public const string Device = "device";
    public const string Solar = "solar";
    public const string Test = "test";
}

public static class AlertDeliveryStatus
{
    /// <summary>Delivered by at least one notification channel.</summary>
    public const string Sent = "sent";
    /// <summary>Every channel failed; SolPnl will try again.</summary>
    public const string Failed = "failed";
    /// <summary>No notification channel is set up yet; it will be sent once one is (within 24 hours).</summary>
    public const string Waiting = "waiting";
    /// <summary>Gave up: no channel was set up, or delivery kept failing, for 24 hours.</summary>
    public const string NotSent = "not-sent";
}

/// <summary>One alert SolPnl raised, and what happened when it tried to send it.</summary>
/// <param name="Severity">"info", "warning" or "critical".</param>
/// <param name="Delivery">Per-channel results, e.g. "Email (SMTP): sent".</param>
public sealed record AlertEntry(
    long Id,
    string Category,
    string Severity,
    string Title,
    string Message,
    string? DeviceId,
    DateTimeOffset RaisedAt,
    DateTimeOffset? DeliveredAt,
    string Status,
    string? Delivery);

/// <summary>A condition that is still going on (e.g. a device that's offline, an active weather alert).</summary>
public sealed record ActiveAlert(string Category, string Severity, string Title, DateTimeOffset Since, long? EntryId);

public sealed record AlertsPage(IReadOnlyList<ActiveAlert> Active, IReadOnlyList<AlertEntry> History, bool HasMore);

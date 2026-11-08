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

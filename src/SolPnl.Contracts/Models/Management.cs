using SolPnl.Contracts.Plugins;

namespace SolPnl.Contracts.Models;

public static class PluginKinds
{
    public const string Device = "device";
    public const string Notification = "notification";
}

/// <summary>An installed plugin and whether it's enabled. Disabled device plugins don't poll any of their devices.</summary>
public sealed record PluginInfo(
    string Id,
    string DisplayName,
    string Description,
    string Kind,
    bool Enabled,
    int DeviceCount,
    IReadOnlyList<SettingField> Settings);

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

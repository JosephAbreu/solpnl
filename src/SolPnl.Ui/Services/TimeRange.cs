namespace SolPnl.Ui.Services;

/// <summary>Preset time ranges for history views ("last N"). The API chooses the resolution.</summary>
public sealed record TimeRange(string Key, string Label, TimeSpan Span)
{
    public static readonly TimeRange LastHour = new("1h", "Last hour", TimeSpan.FromHours(1));
    public static readonly TimeRange Last6Hours = new("6h", "Last 6 hours", TimeSpan.FromHours(6));
    public static readonly TimeRange Last24Hours = new("24h", "Last 24 hours", TimeSpan.FromHours(24));
    public static readonly TimeRange Last7Days = new("7d", "Last 7 days", TimeSpan.FromDays(7));
    public static readonly TimeRange Last30Days = new("30d", "Last 30 days", TimeSpan.FromDays(30));

    public static readonly IReadOnlyList<TimeRange> All = [LastHour, Last6Hours, Last24Hours, Last7Days, Last30Days];

    public static TimeRange FromKey(string? key) => All.FirstOrDefault(r => r.Key == key) ?? Last24Hours;
}

public enum DeviceState
{
    Online,
    Offline,
    /// <summary>Configured and polled, but nothing stored yet.</summary>
    Waiting,
    /// <summary>Configured, but its plugin is disabled.</summary>
    Paused,
    /// <summary>Has stored history but is no longer configured.</summary>
    NotConfigured,
}

/// <summary>Device health from its configuration and the age of its newest reading.</summary>
public static class DeviceStatus
{
    /// <summary>Six missed 10-second polls.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(1);

    public static bool IsOnline(DateTimeOffset? lastReading, DateTimeOffset now) =>
        lastReading is { } t && now - t <= OfflineAfter;

    public static DeviceState For(bool configured, bool pluginEnabled, DateTimeOffset? lastReading, DateTimeOffset now) =>
        !configured ? DeviceState.NotConfigured
        : !pluginEnabled ? DeviceState.Paused
        : lastReading is null ? DeviceState.Waiting
        : IsOnline(lastReading, now) ? DeviceState.Online
        : DeviceState.Offline;
}

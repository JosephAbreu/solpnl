using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Core.Devices;

/// <summary>Reads the fault codes a device reports (<see cref="Metrics.FaultCode"/>.N readings, 0 = none).</summary>
public static class DeviceFaults
{
    /// <summary>Readings older than this don't count: the device has stopped reporting, which is its own alert.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    public static bool IsFaultMetric(string metric) => metric.StartsWith(Metrics.FaultCode + ".", StringComparison.Ordinal);

    /// <summary>
    /// The distinct non-zero codes in <paramref name="latest"/>, lowest first; null when the device doesn't report
    /// faults, or its fault readings are older than <see cref="MaxAge"/> (nothing is known about now).
    /// </summary>
    public static IReadOnlyList<int>? Active(IEnumerable<Reading> latest, DateTimeOffset now)
    {
        var faults = latest.Where(r => IsFaultMetric(r.Metric)).ToList();
        if (faults.Count == 0 || faults.All(r => now - r.Timestamp > MaxAge)) return null;
        return faults
            .Where(r => now - r.Timestamp <= MaxAge && r.Value >= 1)
            .Select(r => (int)Math.Round(r.Value))
            .Distinct()
            .Order()
            .ToList();
    }

    public static string? Describe(IDevicePlugin? plugin, int code) => (plugin as IDeviceFaults)?.DescribeFault(code);

    /// <summary>"fault 09: PV voltage too high", or "fault 41" when the plugin doesn't know the code.</summary>
    public static string Label(int code, string? description) =>
        description is null ? $"fault {code:00}" : $"fault {code:00}: {description}";
}

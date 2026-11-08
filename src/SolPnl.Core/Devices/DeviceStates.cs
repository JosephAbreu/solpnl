using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Core.Devices;

/// <summary>Reads the operating state a device reports (see <see cref="IDeviceState"/>).</summary>
public static class DeviceStates
{
    /// <summary>
    /// The device's state now, described by its plugin; null when the plugin doesn't report a state, or its state
    /// reading is older than <see cref="DeviceFaults.MaxAge"/> (the device has stopped reporting).
    /// </summary>
    public static DeviceOperatingState? Current(IDevicePlugin? plugin, IEnumerable<Reading> latest, DateTimeOffset now)
    {
        if (plugin is not IDeviceState states) return null;
        var reading = latest.FirstOrDefault(r => r.Metric == states.StateMetric);
        if (reading is null || now - reading.Timestamp > DeviceFaults.MaxAge) return null;
        var value = (int)Math.Round(reading.Value);
        return new DeviceOperatingState(value, states.DescribeState(value));
    }
}

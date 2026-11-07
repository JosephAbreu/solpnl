using SolPnl.Contracts.Models;

namespace SolPnl.Contracts.Plugins;

/// <summary>
/// Provides support for a vendor's hardware. The core polls <see cref="ReadAsync"/> on a schedule;
/// plugins should not run their own timers. Control/commands will be added as a separate interface
/// later so read-only plugins stay simple.
/// </summary>
public interface IDevicePlugin : IPlugin
{
    Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the current values from the device, normalized to <see cref="Metrics"/> where possible.</summary>
    Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default);
}

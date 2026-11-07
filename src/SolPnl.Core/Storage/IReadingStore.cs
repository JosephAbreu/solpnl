using SolPnl.Contracts.Models;

namespace SolPnl.Core.Storage;

/// <summary>Persists device readings. Implementations must work on every supported database driver.</summary>
public interface IReadingStore
{
    /// <summary>Saves a poll's readings for one device and updates its latest values.</summary>
    Task SaveAsync(DeviceInfo device, IReadOnlyList<Reading> readings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Latest value of every metric, optionally for a single device.</summary>
    Task<IReadOnlyList<Reading>> GetLatestAsync(string? deviceId = null, CancellationToken cancellationToken = default);

    /// <summary>Raw values of one metric in [from, to], oldest first.</summary>
    Task<IReadOnlyList<HistoryPoint>> GetHistoryAsync(
        string deviceId, string metric, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

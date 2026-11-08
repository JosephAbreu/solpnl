namespace SolPnl.Contracts.Plugins;

/// <summary>
/// Optional for an <see cref="IDevicePlugin"/> that reports an operating state as a numeric reading: names the metric
/// and turns its value into words for the device page.
/// </summary>
public interface IDeviceState
{
    /// <summary>The metric that carries the state, e.g. "vendor.srne.state".</summary>
    string StateMetric { get; }

    /// <summary>A short description of state <paramref name="value"/>, e.g. "Mains powered", or null if unknown.</summary>
    string? DescribeState(int value);
}

namespace SolPnl.Contracts.Plugins;

/// <summary>
/// Optional for an <see cref="IDevicePlugin"/> that reports fault codes (as <c>fault.code.N</c> readings, 0 = none):
/// turns a code into words for alerts and the device page.
/// </summary>
public interface IDeviceFaults
{
    /// <summary>A short description of <paramref name="code"/>, e.g. "PV voltage too high", or null if unknown.</summary>
    string? DescribeFault(int code);
}

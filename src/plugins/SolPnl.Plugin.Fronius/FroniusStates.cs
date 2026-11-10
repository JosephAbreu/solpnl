namespace SolPnl.Plugin.Fronius;

/// <summary>
/// Fronius inverter states (DeviceStatus.StatusCode), as listed in Fronius' Solar API documentation. pyfronius passes
/// the number through without a table, so these names aren't cross-checked against a second source.
/// </summary>
public static class FroniusStates
{
    public static string? Describe(int code) => code switch
    {
        >= 0 and <= 6 => "Starting up",
        7 => "Running",
        8 => "Standby",
        9 => "Bootloading",
        10 => "Error",
        11 => "Idle",
        12 => "Ready",
        13 => "Sleeping",
        _ => null,
    };
}

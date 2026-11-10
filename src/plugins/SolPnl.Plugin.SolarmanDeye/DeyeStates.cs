namespace SolPnl.Plugin.SolarmanDeye;

/// <summary>
/// Deye running states (register 59), in plain words. The same table is used by the open-source Deye readers; none of
/// the values have been seen on a live unit yet.
/// </summary>
public static class DeyeStates
{
    private static readonly string[] Names =
    [
        "Standby",    // 0
        "Self-check", // 1
        "Normal",     // 2
        "Alarm",      // 3
        "Fault",      // 4
    ];

    public static string? Describe(int value) => value >= 0 && value < Names.Length ? Names[value] : null;
}

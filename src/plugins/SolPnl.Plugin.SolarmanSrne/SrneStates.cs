namespace SolPnl.Plugin.SolarmanSrne;

/// <summary>
/// SRNE machine states (register 0x0210), in plain words, from SRNE's hybrid inverter MODBUS protocol (V1.7). Other
/// open-source SRNE readers use the same table.
/// </summary>
/// <remarks>
/// A Sungoldpower SPH10048P in no-battery mode reads 2 ("Initialization") all day and night, while solar and the grid
/// both power the house, so on that model 2 doesn't mean the inverter is still starting up. The other values haven't
/// been seen on a live unit yet.
/// </remarks>
public static class SrneStates
{
    private static readonly string[] Names =
    [
        "Power-up delay",       // 0
        "Waiting",              // 1
        "Initialization",       // 2
        "Soft start",           // 3
        "Mains powered",        // 4
        "Inverter powered",     // 5
        "Switching to mains",   // 6
        "Switching to inverter",// 7
        "Battery activation",   // 8
        "Shut down by user",    // 9
        "Fault",                // 10
    ];

    public static string? Describe(int value) => value >= 0 && value < Names.Length ? Names[value] : null;
}

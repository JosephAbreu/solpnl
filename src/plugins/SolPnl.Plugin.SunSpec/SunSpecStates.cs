namespace SolPnl.Plugin.SunSpec;

/// <summary>SunSpec inverter operating states (the St point of models 101–103 and 111–113), in plain words.</summary>
public static class SunSpecStates
{
    public const int Off = 1;
    public const int Sleeping = 2;
    public const int Standby = 8;

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Off",
        [2] = "Sleeping",
        [3] = "Starting",
        [4] = "Producing",          // MPPT: tracking the panels' maximum power point
        [5] = "Producing (limited)", // THROTTLED
        [6] = "Shutting down",
        [7] = "Fault",
        [8] = "Standby",
    };

    public static string? Describe(int value) => Names.GetValueOrDefault(value);
}

/// <summary>
/// SunSpec inverter events (the Evt1 bit field), reported as fault codes 1–16 (bit 0 = code 1). Vendor-specific
/// events (EvtVnd1..4) differ per manufacturer and aren't reported.
/// </summary>
public static class SunSpecEvents
{
    private static readonly string[] Names =
    [
        "Ground fault",                // bit 0
        "DC voltage too high",         // 1
        "AC disconnect open",          // 2
        "DC disconnect open",          // 3
        "Grid disconnected",           // 4
        "Cabinet open",                // 5
        "Shut down manually",          // 6
        "Too hot",                     // 7
        "Grid frequency too high",     // 8
        "Grid frequency too low",      // 9
        "Grid voltage too high",       // 10
        "Grid voltage too low",        // 11
        "String fuse blown",           // 12
        "Too cold",                    // 13
        "Memory lost",                 // 14
        "Hardware self-test failed",   // 15
    ];

    public static string? Describe(int code) => code >= 1 && code <= Names.Length ? Names[code - 1] : null;

    /// <summary>Codes (bit + 1) of the defined events that are set, lowest first. Bits 16–31 are reserved.</summary>
    public static List<int> ActiveCodes(uint events)
    {
        var codes = new List<int>();
        for (var bit = 0; bit < Names.Length; bit++)
            if ((events & (1u << bit)) != 0) codes.Add(bit + 1);
        return codes;
    }
}

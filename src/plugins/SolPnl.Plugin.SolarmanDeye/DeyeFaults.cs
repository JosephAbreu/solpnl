namespace SolPnl.Plugin.SolarmanDeye;

/// <summary>
/// Deye fault codes (the "F" number the inverter shows, e.g. F13), in plain words, from the fault table in Deye's
/// hybrid inverter manuals. The bit positions in registers 103..106 that ha-solarman names (e.g. bit 12 "Working mode
/// changed", bit 22 "AC current leakage") line up with these codes as F = bit + 1. Codes not listed are reported by
/// number only.
/// </summary>
public static class DeyeFaults
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "PV input reversed",
        [7] = "DC/DC soft start failed",
        [10] = "Auxiliary power supply failure",
        [13] = "Working mode changed",
        [15] = "AC output current too high",
        [16] = "Leakage current fault (GFCI)",
        [18] = "AC output current too high (hardware)",
        [20] = "DC current too high (hardware)",
        [22] = "Emergency stop",
        [23] = "AC leakage current too high",
        [24] = "PV insulation fault",
        [26] = "DC bus voltage imbalance",
        [29] = "Parallel communication fault",
        [34] = "Output overload",
        [35] = "No grid",
        [41] = "Parallel system stopped",
        [42] = "Grid voltage out of range",
        [46] = "Backup battery fault",
        [47] = "Grid frequency too high",
        [48] = "Grid frequency too low",
        [55] = "DC bus voltage too high",
        [56] = "DC bus voltage too low",
        [58] = "Battery (BMS) communication lost",
        [61] = "Turned off with the button",
        [62] = "Stopped by remote control (DRM0)",
        [63] = "Arc fault",
        [64] = "Heat sink too hot",
    };

    public static string? Describe(int code) => Names.GetValueOrDefault(code);
}

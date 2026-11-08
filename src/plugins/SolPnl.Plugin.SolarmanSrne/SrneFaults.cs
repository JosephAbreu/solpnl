namespace SolPnl.Plugin.SolarmanSrne;

/// <summary>
/// SRNE fault codes (the number the inverter shows on its display), in plain words, from SRNE's fault code table
/// for its split-phase and hybrid inverters. Codes not listed are reported by number only; the inverter's manual has
/// the full list for each model.
/// </summary>
public static class SrneFaults
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Battery voltage low",
        [2] = "Battery discharge current too high",
        [3] = "Battery not connected",
        [4] = "Battery voltage too low: discharge stopped",
        [5] = "Battery current too high",
        [6] = "Battery charge voltage too high",
        [7] = "DC bus voltage too high (hardware)",
        [8] = "DC bus voltage too high",
        [9] = "PV voltage too high",
        [10] = "PV boost current too high",
        [11] = "PV boost current too high (hardware)",
        [12] = "Internal communication error",
        [13] = "Overload on grid bypass",
        [14] = "Inverter overload",
        [15] = "Inverter output current too high",
        [16] = "Shutdown requested by the second controller",
        [17] = "Inverter output short circuit",
        [18] = "DC bus soft start failed",
        [19] = "PV charger overheated",
        [20] = "Inverter overheated",
        [21] = "Fan failure",
        [22] = "Memory (EEPROM) error",
        [23] = "Model setting error",
        [24] = "DC bus voltage imbalance",
        [25] = "DC bus short circuit",
        [26] = "Inverter output relay fault",
        [28] = "Grid input phase error",
        [29] = "DC bus voltage too low",
        [30] = "Battery charge below 10%",
        [31] = "Battery charge below 5%",
        [32] = "Battery charge too low: shut down",
    };

    public static string? Describe(int code) => Names.GetValueOrDefault(code);
}

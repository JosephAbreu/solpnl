namespace SolPnl.Plugin.Luxpower;

/// <summary>
/// EG4 / Luxpower status, faults and warnings, from lxp-bridge. Faults (registers 60–61) are the inverter's E codes,
/// reported as 100 + n for E0nn; warnings (62–63) are its W codes, reported as 200 + n for W0nn.
/// </summary>
public static class LuxpowerCodes
{
    private const int FaultBase = 100;
    private const int WarningBase = 200;

    private static readonly string?[] Faults =
    [
        "Internal communication fault 1", "Model fault", "Battery-on MOSFET failure", "CT failure", null, null, null, null,
        "CAN communication error in parallel system", "Master lost in parallel system", "Several masters in parallel system",
        "AC input inconsistent in parallel system", "Backup (UPS) output short circuit", "Reverse current on backup (UPS) output",
        "DC bus short circuit", "Phase error in three-phase system", "Relay check fault", "Internal communication fault 2",
        "Internal communication fault 3", "DC bus voltage too high", "Backup (EPS) connection fault", "PV voltage too high",
        "Overcurrent protection", "Neutral fault", "PV short circuit", "Heat sink too hot", "Internal fault",
        "Main and redundant CPU readings disagree", null, null, null, "Internal communication fault 4",
    ];

    private static readonly string?[] Warnings =
    [
        "Battery communication failure", "Arc fault (AFCI) communication failure", "Arc fault (AFCI) high",
        "Meter communication failure", "Battery forbids both charging and discharging", "Auto test failed", null,
        "Display communication failure", "Firmware version mismatch", "Fan stuck", null, "Parallel number out of range",
        "Battery-on MOSFET", "Too hot", null, "Battery connected in reverse", "Grid power outage",
        "Grid voltage out of range", "Grid frequency out of range", null, "PV insulation low", "Leakage current high",
        "DC injection high", "PV short circuit", null, "Battery voltage too high", "Battery voltage too low",
        "Battery open circuit", "Backup (EPS) overload", "Backup (EPS) voltage too high", "Meter connected in reverse",
        "DC voltage too high",
    ];

    /// <summary>Codes of the set (defined) fault bits, then warning bits, lowest first.</summary>
    public static List<int> Active(uint faults, uint warnings)
    {
        var codes = new List<int>();
        for (var bit = 0; bit < 32; bit++)
            if ((faults & (1u << bit)) != 0 && Faults[bit] is not null) codes.Add(FaultBase + bit);
        for (var bit = 0; bit < 32; bit++)
            if ((warnings & (1u << bit)) != 0 && Warnings[bit] is not null) codes.Add(WarningBase + bit);
        return codes;
    }

    public static string? Describe(int code) => code switch
    {
        >= WarningBase and < WarningBase + 32 when Warnings[code - WarningBase] is { } w => $"W{code - WarningBase:000}: {w}",
        >= FaultBase and < FaultBase + 32 when Faults[code - FaultBase] is { } f => $"E{code - FaultBase:000}: {f}",
        _ => null,
    };

    private static readonly Dictionary<int, string> Statuses = new()
    {
        [0x00] = "Standby",
        [0x02] = "Updating firmware",
        [0x04] = "Solar, on grid",
        [0x08] = "Solar charging",
        [0x0C] = "Solar charging, on grid",
        [0x10] = "Battery, on grid",
        [0x11] = "Bypass",
        [0x14] = "Solar and battery, on grid",
        [0x19] = "Solar charging and bypass",
        [0x20] = "Grid charging",
        [0x28] = "Solar and grid charging",
        [0x40] = "Battery, off grid",
        [0x80] = "Solar, off grid",
        [0xC0] = "Solar and battery, off grid",
        [0x88] = "Solar charging, off grid",
    };

    public static string? DescribeStatus(int value) => Statuses.GetValueOrDefault(value);
}

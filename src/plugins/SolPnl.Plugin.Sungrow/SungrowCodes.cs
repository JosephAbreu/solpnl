namespace SolPnl.Plugin.Sungrow;

/// <summary>
/// Sungrow running states and model names. SG inverters report their state in 5038, SH hybrids in 13000; both use
/// codes from the same table (as merged by mkaiser's Home Assistant configuration).
/// </summary>
public static class SungrowCodes
{
    private static readonly Dictionary<int, string> States = new()
    {
        [0x0000] = "Running",
        [0x0001] = "Stopped",
        [0x0002] = "Stopped",
        [0x0004] = "Emergency stop",
        [0x0008] = "Standby",
        [0x0010] = "Initial standby",
        [0x0014] = "Microgrid operation",
        [0x0020] = "Starting",
        [0x0040] = "Running",
        [0x0041] = "Off-grid charging",
        [0x0080] = "Running (derated)",
        [0x0100] = "Fault",
        [0x0200] = "Update failed",
        [0x0400] = "Running (maintenance mode)",
        [0x0800] = "Running (forced mode)",
        [0x1000] = "Running (off grid)",
        [0x1200] = "Initial standby",
        [0x1300] = "Stopped with the key",
        [0x1400] = "Standby",
        [0x1500] = "Emergency stop",
        [0x1600] = "Starting",
        [0x1700] = "Arc fault self-test shutdown",
        [0x1900] = "Safe mode",
        [0x2500] = "Communication fault",
        [0x2501] = "Restarting",
        [0x4000] = "Running (external EMS)",
        [0x4001] = "Emergency charging",
        [0x5500] = "Fault",
        [0x8000] = "Stopped",
        [0x8100] = "Running (derated)",
        [0x8200] = "Running (dispatched)",
        [0x9100] = "Running with a warning",
    };

    public static string? DescribeState(int value) => States.GetValueOrDefault(value);

    /// <summary>Residential and small commercial models by device type code (5000), from SunGather and mkaiser.</summary>
    private static readonly Dictionary<int, string> Models = new()
    {
        [0x0122] = "SG3K-D", [0x0126] = "SG5K-D", [0x2403] = "SG8K-D",
        [0x2603] = "SG3.0RS", [0x2604] = "SG3.6RS", [0x2605] = "SG4.0RS", [0x2606] = "SG5.0RS", [0x2607] = "SG6.0RS",
        [0x260E] = "SG9.0RS", [0x2609] = "SG10RS",
        [0x243D] = "SG3.0RT", [0x243E] = "SG4.0RT", [0x2430] = "SG5.0RT", [0x2431] = "SG6.0RT", [0x243C] = "SG7.0RT",
        [0x2432] = "SG8.0RT", [0x2433] = "SG10RT", [0x2434] = "SG12RT", [0x2435] = "SG15RT", [0x2436] = "SG17RT",
        [0x2437] = "SG20RT",
        [0x2C10] = "SG30CX", [0x2C00] = "SG33CX", [0x2C0A] = "SG36CX-US", [0x2C01] = "SG40CX", [0x2C02] = "SG50CX",
        [0x2C0B] = "SG60CX-US", [0x2C22] = "SG75CX", [0x2C12] = "SG100CX", [0x2C06] = "SG110CX",
        [0x0D09] = "SH5K-20", [0x0D06] = "SH3K6", [0x0D07] = "SH4K6", [0x0D03] = "SH5K-V13", [0x0D0C] = "SH5K-30",
        [0x0D0A] = "SH3K6-30", [0x0D0B] = "SH4K6-30",
        [0x0D0D] = "SH3.6RS", [0x0D0E] = "SH4.6RS", [0x0D0F] = "SH5.0RS", [0x0D10] = "SH6.0RS", [0x0D17] = "SH3.0RS",
        [0x0D18] = "SH4.0RS", [0x0D1A] = "SH8.0RS", [0x0D1B] = "SH10RS",
        [0x0E00] = "SH5.0RT", [0x0E01] = "SH6.0RT", [0x0E02] = "SH8.0RT", [0x0E03] = "SH10RT",
        [0x0E0C] = "SH5.0RT-V112", [0x0E0D] = "SH6.0RT-V112", [0x0E0E] = "SH8.0RT-V112", [0x0E0F] = "SH10RT-V112",
        [0x0E08] = "SH5.0RT-V122", [0x0E09] = "SH6.0RT-V122", [0x0E0A] = "SH8.0RT-V122", [0x0E0B] = "SH10RT-V122",
        [0x0E10] = "SH5.0RT-20", [0x0E11] = "SH6.0RT-20", [0x0E12] = "SH8.0RT-20", [0x0E13] = "SH10RT-20",
        [0x0E20] = "SH5T", [0x0E21] = "SH6T", [0x0E22] = "SH8T", [0x0E23] = "SH10T", [0x0E24] = "SH12T",
        [0x0E25] = "SH15T", [0x0E26] = "SH20T", [0x0E27] = "SH25T",
    };

    public static string DescribeModel(ushort deviceType) =>
        Models.TryGetValue(deviceType, out var name) ? name : $"Sungrow inverter (type 0x{deviceType:X4})";
}

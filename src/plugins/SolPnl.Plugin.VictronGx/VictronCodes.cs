namespace SolPnl.Plugin.VictronGx;

/// <summary>
/// Fault codes the plugin reports (fault.code.N), in plain words. One number space covers three Victron sources:
/// VE.Bus errors as Victron numbers them (1–99), VE.Bus alarms (101–103), and solar charger errors (300 + the
/// charger's error code). Texts follow Victron's register definitions (dbus_modbustcp attributes.csv).
/// </summary>
public static class VictronCodes
{
    private const int AlarmBase = 100;
    private const int SolarChargerBase = 300;

    /// <summary>Alarm registers 34 (high temperature), 35 (low battery), 36 (overload) → codes 101–103.</summary>
    public static int AlarmCode(ushort register) => AlarmBase + register - 33;

    public static int SolarChargerCode(int errorCode) => SolarChargerBase + errorCode;

    private static readonly Dictionary<int, string> VebusErrors = new()
    {
        [1] = "Switched off because another phase switched off",
        [2] = "New and old MK2 types mixed in the system",
        [3] = "Not the expected number of VE.Bus devices found",
        [4] = "No other VE.Bus device found",
        [5] = "Overvoltage on AC out",
        [6] = "Error in DDC program",
        [7] = "BMS connected without the required Assistant",
        [10] = "System time synchronisation problem",
        [14] = "Device can't transmit data",
        [16] = "Dongle missing",
        [17] = "A device took over as master because the master failed",
        [18] = "AC overvoltage on a slave output",
        [22] = "Device can't work as a slave",
        [24] = "Switch-over protection started",
        [25] = "Firmware incompatible between connected devices",
        [26] = "Internal error",
    };

    private static readonly Dictionary<int, string> Alarms = new()
    {
        [101] = "Inverter/charger too hot",
        [102] = "Battery low",
        [103] = "Inverter/charger overloaded",
    };

    private static readonly Dictionary<int, string> SolarChargerErrors = new()
    {
        [1] = "Battery too hot",
        [2] = "Battery voltage too high",
        [3] = "Battery temperature sensor miswired (+)",
        [4] = "Battery temperature sensor miswired (-)",
        [5] = "Battery temperature sensor disconnected",
        [6] = "Battery voltage sense miswired (+)",
        [7] = "Battery voltage sense miswired (-)",
        [8] = "Battery voltage sense disconnected",
        [9] = "Battery cable losses too high",
        [17] = "Charger too hot",
        [18] = "Charger current too high",
        [19] = "Charger current polarity reversed",
        [20] = "Bulk charge time limit reached",
        [22] = "Charger temperature sensor miswired",
        [23] = "Charger temperature sensor disconnected",
        [34] = "Solar input current too high",
    };

    public static string? Describe(int code) => code switch
    {
        > SolarChargerBase => SolarChargerErrors.TryGetValue(code - SolarChargerBase, out var s) ? $"Solar charger: {s}" : null,
        > AlarmBase => Alarms.GetValueOrDefault(code),
        _ => VebusErrors.GetValueOrDefault(code),
    };

    private static readonly Dictionary<int, string> VebusStates = new()
    {
        [0] = "Off",
        [1] = "Low power",
        [2] = "Fault",
        [3] = "Charging (bulk)",
        [4] = "Charging (absorption)",
        [5] = "Charging (float)",
        [6] = "Storage",
        [7] = "Equalizing",
        [8] = "Passing through",
        [9] = "Inverting",
        [10] = "Power assist",
        [11] = "Power supply",
        [252] = "External control",
    };

    /// <summary>VE.Bus state (register 31).</summary>
    public static string? DescribeState(int value) => VebusStates.GetValueOrDefault(value);
}

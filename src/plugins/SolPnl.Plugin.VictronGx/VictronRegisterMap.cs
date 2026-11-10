using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.VictronGx;

/// <summary>
/// Registers of a Victron GX device (Cerbo GX, Venus GX, CCGX, MultiPlus-II GX, ...) from Victron's own Modbus TCP
/// register definitions (dbus_modbustcp attributes.csv). Victron's scale convention: value = raw ÷ scale, so scale
/// 10 is tenths and scale 0.1 means the raw value counts tens.
/// </summary>
/// <remarks>
/// Each block contains only defined registers: reading a reserved register can fail the whole read. Not yet
/// verified against a live GX device.
/// </remarks>
public static class VictronRegisterMap
{
    /// <summary>The system overview (com.victronenergy.system) is always on unit ID 100.</summary>
    public const byte SystemUnitId = 100;

    /// <summary>
    /// The usual unit IDs of the VE.Bus inverter/charger, from Victron's unit ID table: Cerbo GX (227),
    /// MultiPlus-II GX / EasySolar-II GX (228), CCGX (246), Venus GX (242).
    /// </summary>
    public static readonly IReadOnlyList<byte> VebusUnitCandidates = [227, 228, 246, 242];

    public static readonly IReadOnlyList<(ushort Start, ushort Count)> SystemBlocks =
    [
        (808, 19), // AC PV, consumption, grid, genset per phase, active input source: 808..826
        (840, 5),  // battery voltage, current, power, SOC, state: 840..844
        (850, 1),  // DC PV power
        (860, 1),  // DC system power
    ];

    /// <summary>Serial number of the GX device (string[6]).</summary>
    public static readonly (ushort Start, ushort Count) SystemSerialBlock = (800, 6);

    public static readonly IReadOnlyList<(ushort Start, ushort Count)> VebusBlocks =
    [
        (3, 34), // AC in, AC out, DC, phases, active input, SOC, state, error, mode, alarms: 3..36
    ];

    /// <summary>777 (PV current) is internal and not exposed, so it's split around.</summary>
    public static readonly IReadOnlyList<(ushort Start, ushort Count)> SolarChargerBlocks =
    [
        (771, 6),  // battery voltage, current, temperature, mode, state, PV voltage: 771..776
        (784, 6),  // yield today, max power today, yield yesterday, max power yesterday, error code, PV power: 784..789
    ];

    public const string StateMetric = "vendor.victron.vebus_state";
    public const string ActiveInputMetric = "vendor.victron.active_input";
    public const string GensetPowerMetric = "vendor.victron.genset_power";
    public const string DcSystemPowerMetric = "vendor.victron.dc_system_power";
    public const string AcPvPowerMetric = "vendor.victron.ac_pv_power";
    public const string DcPvPowerMetric = "vendor.victron.dc_pv_power";
    public const string ChargerYieldTodayMetric = "vendor.victron.charger_yield_today";
    public const string ChargerStateMetric = "vendor.victron.charger_state";

    public const int FaultSlots = 4;

    /// <summary>Below this battery voltage the battery is treated as absent (the register reads 0 without one).</summary>
    public const double BatteryPresentMinVoltage = 5.0;

    public sealed record Inputs(
        IReadOnlyDictionary<ushort, ushort> System,
        IReadOnlyDictionary<ushort, ushort>? Vebus,
        IReadOnlyList<IReadOnlyDictionary<ushort, ushort>> SolarChargers);

    public static IReadOnlyList<Reading> Decode(Inputs inputs, string deviceId, DateTimeOffset timestamp)
    {
        var readings = new List<Reading>();

        void Add(string metric, double? value, string unit)
        {
            if (value is not { } v) return;
            readings.Add(new Reading(deviceId, metric, Math.Round(v, 3), unit, timestamp));
        }

        var sys = inputs.System;
        var vebus = inputs.Vebus;

        // Phases: the VE.Bus system says how many; without one, the system's per-phase values for L2/L3 read 0.
        var phases = vebus is not null && U16(vebus, 28) is >= 1 and <= 3 and var n ? (int)n : 1;

        // ---- System overview (unit 100) ----
        double? Sum(ushort first, bool signed)
        {
            double? total = null;
            for (var i = 0; i < phases; i++)
                if ((signed ? S16(sys, (ushort)(first + i)) : U16(sys, (ushort)(first + i))) is { } v) total = (total ?? 0) + v;
            return total;
        }

        // AC-coupled PV inverters on the output, on the grid input and on the generator input.
        var acPvParts = new[] { Sum(808, false), Sum(811, false), Sum(814, false) }.OfType<double>().ToList();
        double? acPv = acPvParts.Count > 0 ? acPvParts.Sum() : null;
        var dcPv = U16(sys, 850);
        Add(AcPvPowerMetric, acPv, "W");
        Add(DcPvPowerMetric, dcPv, "W");
        if (acPv is not null || dcPv is not null) Add(PvPower, (acPv ?? 0) + (dcPv ?? 0), "W");

        for (var i = 1; i <= phases; i++)
        {
            Add(ForPhase(LoadPower, i), U16(sys, (ushort)(816 + i)), "W");
            Add(ForPhase(GridPower, i), S16(sys, (ushort)(819 + i)), "W"); // + from the grid, - to the grid
        }
        Add(LoadPower, Sum(817, false), "W");
        Add(GridPower, Sum(820, true), "W");
        Add(GensetPowerMetric, Sum(823, true), "W");
        Add(ActiveInputMetric, S16(sys, 826), "");
        Add(DcSystemPowerMetric, S16(sys, 860), "W");

        if (U16(sys, 840) / 10 is >= BatteryPresentMinVoltage and var batteryVolts)
        {
            Add(BatteryVoltage, batteryVolts, "V");
            Add(BatteryCurrent, S16(sys, 841) / 10, "A");  // + charging
            Add(BatteryPower, S16(sys, 842), "W");         // + charging
            Add(BatterySoc, U16(sys, 843), "%");
        }

        // ---- VE.Bus inverter/charger ----
        var faults = new List<int>();
        if (vebus is not null)
        {
            for (var i = 1; i <= phases; i++)
            {
                var o = (ushort)(i - 1);
                // The active AC input: 0 V when the grid (or generator) is gone and the Multi is inverting.
                Add(ForPhase(GridVoltage, i), U16(vebus, (ushort)(3 + o)) / 10, "V");
                Add(ForPhase(GridCurrent, i), S16(vebus, (ushort)(6 + o)) / 10, "A");
                Add(ForPhase(InverterVoltage, i), U16(vebus, (ushort)(15 + o)) / 10, "V");
                Add(ForPhase(InverterCurrent, i), S16(vebus, (ushort)(18 + o)) / 10, "A");
            }
            Add(GridFrequency, S16(vebus, 9) / 100, "Hz");
            Add(InverterFrequency, S16(vebus, 21) / 100, "Hz");
            Add(StateMetric, U16(vebus, 31), "");

            if (U16(vebus, 32) is > 0 and var error) faults.Add((int)error);
            for (ushort alarm = 34; alarm <= 36; alarm++)
                if (U16(vebus, alarm) == 2) faults.Add(VictronCodes.AlarmCode(alarm));
        }

        // ---- Solar chargers (one per PV input) ----
        for (var i = 0; i < inputs.SolarChargers.Count; i++)
        {
            var c = inputs.SolarChargers[i];
            var volts = U16(c, 776) / 100;
            var watts = U16(c, 789) / 10;
            Add(ForString(PvVoltage, i + 1), volts, "V");
            Add(ForString(PvPower, i + 1), watts, "W");
            // PV current isn't exposed over Modbus; derive it.
            Add(ForString(PvCurrent, i + 1), volts is > 1 && watts is { } w ? w / volts : null, "A");
            Add(ForString(ChargerYieldTodayMetric, i + 1), U16(c, 784) / 10, "kWh");
            Add(ForString(ChargerStateMetric, i + 1), U16(c, 775), "");
            if (U16(c, 788) is > 0 and var e) faults.Add(VictronCodes.SolarChargerCode((int)e));
        }

        if (vebus is not null || inputs.SolarChargers.Count > 0)
        {
            for (var slot = 1; slot <= FaultSlots; slot++)
                Add(ForString(FaultCode, slot), slot <= faults.Count ? faults[slot - 1] : 0, "");
        }

        return readings;
    }

    /// <summary>ASCII, two characters per register, high byte first.</summary>
    public static string? DecodeSerial(ushort[] registers)
    {
        var chars = registers.SelectMany(r => new[] { (char)(r >> 8), (char)(r & 0xFF) })
            .TakeWhile(c => c != '\0').Where(c => c is >= ' ' and <= '~').ToArray();
        var serial = new string(chars).Trim();
        return serial.Length > 0 ? serial : null;
    }

    private static double? U16(IReadOnlyDictionary<ushort, ushort> r, ushort address) =>
        r.TryGetValue(address, out var v) ? v : null;

    private static double? S16(IReadOnlyDictionary<ushort, ushort> r, ushort address) =>
        r.TryGetValue(address, out var v) ? (short)v : null;
}

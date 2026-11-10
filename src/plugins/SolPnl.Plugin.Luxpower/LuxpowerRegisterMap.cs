using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.Luxpower;

/// <summary>
/// Input registers of EG4 / Luxpower hybrid inverters (EG4 18kPV, 12kPV, 6000XP, LXP-LB, ...), as decoded by
/// lxp-bridge. Power values are unsigned magnitudes with a separate register for each direction (to grid / from
/// grid, charge / discharge), so there are no sign conventions to guess. 32-bit values are low word first.
/// </summary>
/// <remarks>Not yet verified against a live inverter.</remarks>
public static class LuxpowerRegisterMap
{
    public static readonly IReadOnlyList<(ushort Start, ushort Count)> Blocks = [(0, 40), (40, 40), (80, 40), (120, 40)];

    public const string StatusMetric = "vendor.luxpower.status";
    public const string InverterPowerMetric = "vendor.luxpower.inverter_power";
    public const string RectifierPowerMetric = "vendor.luxpower.rectifier_power";
    public const string BackupPowerMetric = "vendor.luxpower.eps_power";
    public const string GenPowerMetric = "vendor.luxpower.gen_power";
    public const string BatteryTemperatureMetric = "vendor.luxpower.battery_temperature";
    public const string PvEnergyTodayMetric = "vendor.luxpower.energy.pv_today";
    public const string GridImportTodayMetric = "vendor.luxpower.energy.grid_import_today";
    public const string GridExportTodayMetric = "vendor.luxpower.energy.grid_export_today";
    public const string BatteryChargeTodayMetric = "vendor.luxpower.energy.battery_charge_today";
    public const string BatteryDischargeTodayMetric = "vendor.luxpower.energy.battery_discharge_today";

    public const int FaultSlots = 4;
    public const double BatteryPresentMinVoltage = 5.0;

    public static IReadOnlyList<Reading> Decode(IReadOnlyDictionary<ushort, ushort> r, string deviceId, DateTimeOffset timestamp)
    {
        var readings = new List<Reading>();
        void Add(string metric, double? value, string unit)
        {
            if (value is { } v) readings.Add(new Reading(deviceId, metric, Math.Round(v, 3), unit, timestamp));
        }

        Add(StatusMetric, U16(r, 0), "");

        // ---- PV: three inputs; the third only on models that have one ----
        double? pvTotal = null;
        for (var i = 1; i <= 3; i++)
        {
            var volts = U16(r, (ushort)i) / 10;
            var watts = U16(r, (ushort)(6 + i));
            if (watts is { } w) pvTotal = (pvTotal ?? 0) + w;
            if (i == 3 && volts is not > 0) continue;
            Add(ForString(PvVoltage, i), volts, "V");
            Add(ForString(PvPower, i), watts, "W");
            Add(ForString(PvCurrent, i), volts is > 1 && watts is { } p ? p / volts : null, "A");
        }
        Add(PvPower, pvTotal, "W");

        // ---- Grid: separate magnitudes for each direction ----
        var toUser = U16(r, 27);  // from the grid to the house
        var toGrid = U16(r, 26);  // to the grid
        double? grid = toUser is { } imp && toGrid is { } exp ? imp - exp : null;
        Add(GridPower, grid, "W"); // + import
        Add(GridVoltage, U16(r, 12) / 10, "V");
        Add(GridFrequency, U16(r, 15) / 100, "Hz");

        // ---- Inverter output (to grid side) and rectifier (grid charging the battery) ----
        var inverter = U16(r, 16);
        var rectifier = U16(r, 17);
        Add(InverterPowerMetric, inverter, "W");
        Add(RectifierPowerMetric, rectifier, "W");

        // No load register: the house uses what the inverter delivers, less what it draws to charge, plus the grid's
        // net import (the formula the Luxpower integrations use).
        if (inverter is { } inv && rectifier is { } rec && grid is { } g)
            Add(LoadPower, Math.Max(0, inv - rec + g), "W");

        // ---- Backup (EPS) output: split-phase US models report each leg ----
        if (U16(r, 127) is > 0)
        {
            Add(ForPhase(InverterVoltage, 1), U16(r, 127) / 10, "V");
            Add(ForPhase(InverterVoltage, 2), U16(r, 128) / 10, "V");
        }
        else
            Add(InverterVoltage, U16(r, 20) / 10, "V");
        Add(InverterFrequency, U16(r, 23) / 100, "Hz");
        Add(BackupPowerMetric, U16(r, 24), "W");

        // ---- Battery ----
        if (U16(r, 4) / 10 is >= BatteryPresentMinVoltage and var batteryVolts)
        {
            var charge = U16(r, 10);
            var discharge = U16(r, 11);
            double? power = charge is { } c && discharge is { } d ? c - d : null;
            Add(BatteryVoltage, batteryVolts, "V");
            Add(BatterySoc, r.TryGetValue(5, out var socSoh) ? socSoh & 0xFF : null, "%");
            Add(BatteryPower, power, "W"); // + charging
            Add(BatteryCurrent, power is { } p ? p / batteryVolts : null, "A");
            Add(BatteryTemperatureMetric, S16(r, 67), "°C");
        }

        // ---- Temperatures (whole °C): inside, two heat sinks ----
        double? hottest = null;
        for (var i = 0; i < 3; i++)
        {
            if (S16(r, (ushort)(64 + i)) is not { } t || t == 0) continue;
            Add(ForString(InverterTemperature, i + 1), t, "°C");
            hottest = Math.Max(hottest ?? double.MinValue, t);
        }
        Add(InverterTemperature, hottest, "°C");

        Add(GenPowerMetric, U16(r, 123), "W");

        // ---- Faults and warnings ----
        if (U32(r, 60) is { } faults && U32(r, 62) is { } warnings)
        {
            var codes = LuxpowerCodes.Active(faults, warnings);
            for (var slot = 1; slot <= FaultSlots; slot++)
                Add(ForString(FaultCode, slot), slot <= codes.Count ? codes[slot - 1] : 0, "");
        }

        // ---- Today's energy ----
        Add(PvEnergyTodayMetric, Sum(r, 28, 29, 30) / 10, "kWh");
        Add(GridExportTodayMetric, U16(r, 36) / 10, "kWh");
        Add(GridImportTodayMetric, U16(r, 37) / 10, "kWh");
        Add(BatteryChargeTodayMetric, U16(r, 33) / 10, "kWh");
        Add(BatteryDischargeTodayMetric, U16(r, 34) / 10, "kWh");

        return readings;
    }

    private static double? Sum(IReadOnlyDictionary<ushort, ushort> r, params ushort[] addresses) =>
        addresses.All(r.ContainsKey) ? addresses.Sum(a => (double)r[a]) : null;

    private static double? U16(IReadOnlyDictionary<ushort, ushort> r, ushort a) => r.TryGetValue(a, out var v) ? v : null;

    private static double? S16(IReadOnlyDictionary<ushort, ushort> r, ushort a) => r.TryGetValue(a, out var v) ? (short)v : null;

    /// <summary>Low word first.</summary>
    private static uint? U32(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        r.TryGetValue(a, out var lo) && r.TryGetValue((ushort)(a + 1), out var hi) ? (uint)hi << 16 | lo : null;
}

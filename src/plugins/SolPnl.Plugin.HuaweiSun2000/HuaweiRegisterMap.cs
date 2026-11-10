using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.HuaweiSun2000;

/// <summary>The inverter's identity and layout, read once.</summary>
public sealed record HuaweiIdentity(string? Model, string? SerialNumber, int PvStrings, double? RatedPowerWatts);

/// <summary>
/// Register map and decoder for Huawei SUN2000 inverters, with an optional Huawei power meter (DTSU666-H,
/// DDSU666-H) and LUNA2000 battery, from the open-source huawei-solar-lib (the library behind the Huawei Solar
/// Home Assistant integration). Holding registers, addressed as numbered; 32-bit values high word first.
/// </summary>
/// <remarks>
/// Not yet verified against a live inverter. The library doesn't state the meter's or the battery's sign
/// convention; the ones used here are noted at each and need checking on a live unit.
/// </remarks>
public static class HuaweiRegisterMap
{
    /// <summary>Huawei inverters accept at most 64 registers per read.</summary>
    public const int MaxRegistersPerRead = 64;

    public static readonly IReadOnlyList<(ushort Start, ushort Count)> IdentityBlocks =
    [
        (30000, 25), // model name (15), serial number (10)
        (30070, 5),  // model ID, PV strings, MPPTs, rated power (U32)
    ];

    /// <summary>Inverter blocks read every poll; the PV block's length depends on the number of strings.</summary>
    public static IReadOnlyList<(ushort Start, ushort Count)> InverterBlocks(int pvStrings) =>
    [
        (32000, 11),                                       // state 1-3, alarms 1-3: 32000..32010
        (32016, (ushort)(2 * Math.Clamp(pvStrings, 1, 24))), // PV voltage/current pairs
        (32064, 27),                                       // input power .. fault code: 32064..32090
        (32114, 2),                                        // daily yield
    ];

    /// <summary>Power meter (if any): status .. per-phase power, 37100..37137.</summary>
    public static readonly (ushort Start, ushort Count) MeterBlock = (37100, 38);

    /// <summary>Battery (if any): rated capacity .. charge/discharge power, 37758..37766; then today's energy.</summary>
    public static readonly IReadOnlyList<(ushort Start, ushort Count)> BatteryBlocks = [(37758, 9), (37784, 4)];

    public const string StateMetric = "vendor.huawei.device_status";
    public const string AcPowerMetric = "vendor.huawei.ac_power";
    public const string EfficiencyMetric = "vendor.huawei.efficiency";
    public const string PvEnergyTodayMetric = "vendor.huawei.energy.pv_today";
    public const string GridImportTotalMetric = "vendor.huawei.energy.grid_import_total";
    public const string GridExportTotalMetric = "vendor.huawei.energy.grid_export_total";
    public const string BatteryChargeTodayMetric = "vendor.huawei.energy.battery_charge_today";
    public const string BatteryDischargeTodayMetric = "vendor.huawei.energy.battery_discharge_today";
    public const string BatteryStatusMetric = "vendor.huawei.battery_status";

    public const int FaultSlots = 4;

    public static HuaweiIdentity DecodeIdentity(IReadOnlyDictionary<ushort, ushort> r) => new(
        Text(r, 30000, 15),
        Text(r, 30015, 10),
        U16(r, 30071) is >= 1 and <= 24 and var n ? (int)n : 2,
        U32(r, 30073));

    public static IReadOnlyList<Reading> Decode(
        IReadOnlyDictionary<ushort, ushort> r, HuaweiIdentity identity, string deviceId, DateTimeOffset timestamp)
    {
        var readings = new List<Reading>();
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string metric, double? value, string unit)
        {
            if (value is not { } v) return;
            v = Math.Round(v, 3);
            values[metric] = v;
            readings.Add(new Reading(deviceId, metric, v, unit, timestamp));
        }

        // ---- Inverter ----
        Add(StateMetric, U16(r, 32089), "");

        for (var i = 1; i <= identity.PvStrings; i++)
        {
            var at = (ushort)(32016 + 2 * (i - 1));
            var v = S16(r, at) / 10;
            var a = S16(r, (ushort)(at + 1)) / 100;
            Add(ForString(PvVoltage, i), v, "V");
            Add(ForString(PvCurrent, i), a, "A");
            Add(ForString(PvPower, i), v * a is { } w ? Math.Max(0, w) : null, "W");
        }

        // Input power is the DC power from the panels; active power is the AC output, which on a hybrid also
        // carries battery discharge, so it isn't "solar".
        Add(PvPower, S32(r, 32064) is { } input ? Math.Max(0, input) : null, "W");
        var ac = S32(r, 32080);
        Add(AcPowerMetric, ac, "W");

        // Single-phase models (SUN2000-L1) read 0 on phases B and C.
        var phases = U16(r, 32070) is > 0 ? 3 : 1;
        for (var i = 1; i <= phases; i++)
        {
            Add(ForPhase(InverterVoltage, i), U16(r, (ushort)(32068 + i)) / 10, "V");
            Add(ForPhase(InverterCurrent, i), S32(r, (ushort)(32072 + 2 * (i - 1))) / 1000, "A");
        }
        Add(InverterFrequency, U16(r, 32085) / 100, "Hz");
        Add(EfficiencyMetric, U16(r, 32086) / 100, "%");
        var temperature = S16(r, 32087) / 10;
        Add(ForString(InverterTemperature, 1), temperature, "°C");
        Add(InverterTemperature, temperature, "°C");
        Add(PvEnergyTodayMetric, U32(r, 32114) / 100, "kWh");

        if (r.ContainsKey(32008))
        {
            var codes = HuaweiCodes.ActiveAlarms(U16(r, 32008) ?? 0, U16(r, 32009) ?? 0, U16(r, 32010) ?? 0);
            for (var slot = 1; slot <= FaultSlots; slot++)
                Add(ForString(FaultCode, slot), slot <= codes.Count ? codes[slot - 1] : 0, "");
        }

        // ---- Power meter (status 1 = normal) ----
        if (U16(r, 37100) == 1)
        {
            var meterPhases = U16(r, 37125) == 1 ? 3 : 1;
            for (var i = 1; i <= meterPhases; i++)
            {
                var o = (ushort)(2 * (i - 1));
                // The meter is powered by the grid, so (unlike the inverter, which sleeps at night) its voltage is
                // the grid's: 0 V in an outage.
                Add(ForPhase(GridVoltage, i), S32(r, (ushort)(37101 + o)) / 10, "V");
                Add(ForPhase(GridCurrent, i), S32(r, (ushort)(37107 + o)) / 100, "A");
                if (meterPhases == 3) Add(ForPhase(GridPower, i), -S32(r, (ushort)(37132 + o)), "W");
            }
            // Huawei meters report power fed to the grid as positive; SolPnl's grid power is + import. Unverified.
            var grid = -S32(r, 37113);
            Add(GridPower, grid, "W");
            Add(GridFrequency, S16(r, 37118) / 100, "Hz");
            Add(GridExportTotalMetric, S32(r, 37119) is { } e ? Math.Abs(e) / 100 : null, "kWh");
            Add(GridImportTotalMetric, S32(r, 37121) / 100, "kWh");

            // The house uses what the inverter delivers plus what comes from the grid.
            if (ac is { } acW && grid is { } g) Add(LoadPower, Math.Max(0, acW + g), "W");
        }

        // ---- Battery (rated capacity 0 = none) ----
        if (U32(r, 37758) is > 0)
        {
            Add(BatterySoc, U16(r, 37760) / 10, "%");
            Add(BatteryStatusMetric, U16(r, 37762), "");
            Add(BatteryVoltage, U16(r, 37763) / 10, "V");
            Add(BatteryCurrent, S16(r, 37764) / 10, "A");   // + charging, as the power below (unverified)
            Add(BatteryPower, S32(r, 37765), "W");          // + charging, - discharging
            Add(BatteryChargeTodayMetric, U32(r, 37784) / 100, "kWh");
            Add(BatteryDischargeTodayMetric, U32(r, 37786) / 100, "kWh");
        }

        return readings;
    }

    private static string? Text(IReadOnlyDictionary<ushort, ushort> r, ushort start, int count)
    {
        var chars = new List<char>();
        for (var a = start; a < start + count; a++)
        {
            if (!r.TryGetValue(a, out var v)) return null;
            chars.Add((char)(v >> 8));
            chars.Add((char)(v & 0xFF));
        }
        var text = new string(chars.TakeWhile(c => c != '\0').Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
        return text.Length > 0 ? text : null;
    }

    private static double? U16(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        r.TryGetValue(a, out var v) && v != 0xFFFF ? v : null;

    private static double? S16(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        r.TryGetValue(a, out var v) && v != 0x7FFF ? (short)v : null;

    private static double? U32(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        Raw32(r, a) is { } v && v != 0xFFFFFFFF ? v : null;

    private static double? S32(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        Raw32(r, a) is { } v && v != 0x7FFFFFFF ? (int)v : null;

    /// <summary>High word first.</summary>
    private static uint? Raw32(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        r.TryGetValue(a, out var hi) && r.TryGetValue((ushort)(a + 1), out var lo) ? (uint)hi << 16 | lo : null;
}

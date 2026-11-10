using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.Sungrow;

/// <summary>The two Sungrow families this plugin reads: SG grid-tied string inverters and SH hybrids.</summary>
public enum SungrowFamily { StringInverter, Hybrid }

/// <summary>
/// Register map and decoder for Sungrow inverters over Modbus TCP (WiNet-S dongle or the inverter's LAN port). All
/// values are input registers (function 0x04). Addresses here are Sungrow's documented register numbers, which are
/// one more than the Modbus address on the wire (see <see cref="ToWireAddress"/>). 32-bit values are low word first.
/// </summary>
/// <remarks>
/// Not yet verified against a live inverter. Addresses and scales are those of two open-source Sungrow readers
/// (SunGather and mkaiser's Home Assistant Modbus configuration), which agree on them.
/// </remarks>
public static class SungrowRegisterMap
{
    public static ushort ToWireAddress(ushort documented) => (ushort)(documented - 1);

    /// <summary>Read once: serial number (4990, 10 registers of ASCII) and device type + nominal power (5000, 5001).</summary>
    public static readonly IReadOnlyList<(ushort Start, ushort Count)> IdentityBlocks = [(4990, 10), (5000, 2)];

    /// <summary>
    /// Read every poll. Kept inside the ranges SunGather reads, because WiNet-S dongles fail reads that cross into
    /// ranges they don't serve.
    /// </summary>
    public static IReadOnlyList<(ushort Start, ushort Count)> Blocks(SungrowFamily family) => family switch
    {
        SungrowFamily.Hybrid => [(5003, 36), (13000, 39)],
        _ => [(5003, 36), (5083, 18)],
    };

    public const string StateMetric = "vendor.sungrow.state";
    public const string DcPowerMetric = "vendor.sungrow.dc_power";
    public const string AcPowerMetric = "vendor.sungrow.ac_power";
    public const string PvEnergyTodayMetric = "vendor.sungrow.energy.pv_today";
    public const string GridImportTodayMetric = "vendor.sungrow.energy.grid_import_today";
    public const string GridExportTodayMetric = "vendor.sungrow.energy.grid_export_today";
    public const string BatteryChargeTodayMetric = "vendor.sungrow.energy.battery_charge_today";
    public const string BatteryDischargeTodayMetric = "vendor.sungrow.energy.battery_discharge_today";
    public const string BatteryTemperatureMetric = "vendor.sungrow.battery_temperature";

    /// <summary>Hybrid grid state (13030).</summary>
    public const ushort OnGrid = 0x55, OffGrid = 0xAA;

    /// <summary>Hybrid power flow bits (13001).</summary>
    [Flags]
    public enum PowerFlow : ushort
    {
        PvGenerating = 0x01, BatteryCharging = 0x02, BatteryDischarging = 0x04, Load = 0x08, Exporting = 0x10, Importing = 0x20,
    }

    /// <summary>SH codes are 0x0Dxx (single phase) and 0x0Exx (three phase); everything else is an SG string inverter.</summary>
    public static SungrowFamily FamilyOf(ushort deviceType) =>
        (deviceType >> 8) is 0x0D or 0x0E ? SungrowFamily.Hybrid : SungrowFamily.StringInverter;

    public static IReadOnlyList<Reading> Decode(
        IReadOnlyDictionary<ushort, ushort> r, SungrowFamily family, string deviceId, DateTimeOffset timestamp)
    {
        var readings = new List<Reading>();
        void Add(string metric, double? value, string unit)
        {
            if (value is { } v) readings.Add(new Reading(deviceId, metric, Math.Round(v, 3), unit, timestamp));
        }

        // ---- Shared 5003..5038 ----
        Add(StateMetric, family == SungrowFamily.Hybrid ? U16(r, 13000) : U16(r, 5038), "");
        Add(PvEnergyTodayMetric, U16(r, 5003) * 0.1, "kWh");

        var temperature = S16(r, 5008) * 0.1;
        Add(ForString(InverterTemperature, 1), temperature, "°C");
        Add(InverterTemperature, temperature, "°C");

        for (var i = 1; i <= 2; i++)
        {
            var v = U16(r, (ushort)(5009 + 2 * i)) * 0.1;
            var a = U16(r, (ushort)(5010 + 2 * i)) * 0.1;
            Add(ForString(PvVoltage, i), v, "V");
            Add(ForString(PvCurrent, i), a, "A");
            Add(ForString(PvPower, i), v * a, "W");
        }

        var dcPower = S32(r, 5017); // documented unsigned; reads signed
        Add(DcPowerMetric, dcPower, "W");

        // Single-phase models read 0 (or "not available") on phases B and C.
        var phases = U16(r, 5020) is > 0 ? 3 : 1;
        var phaseVolts = new double?[3];
        for (var i = 1; i <= phases; i++)
        {
            phaseVolts[i - 1] = U16(r, (ushort)(5018 + i)) * 0.1;
            Add(ForPhase(InverterVoltage, i), phaseVolts[i - 1], "V");
            Add(ForPhase(InverterCurrent, i), S16(r, (ushort)(5021 + i)) * 0.1, "A");
        }

        if (family == SungrowFamily.StringInverter)
            DecodeStringInverter(r, dcPower, Add);
        else
            DecodeHybrid(r, dcPower, phases, phaseVolts, Add);

        return readings;
    }

    private static void DecodeStringInverter(IReadOnlyDictionary<ushort, ushort> r, double? dcPower, Action<string, double?, string> add)
    {
        // A grid-tied inverter's AC output is the solar it delivers.
        var ac = U32(r, 5031);
        add(AcPowerMetric, ac, "W");
        add(PvPower, ac ?? (dcPower is { } dc ? Math.Max(0, dc) : null), "W");
        add(InverterFrequency, U16(r, 5036) * 0.1, "Hz");

        // With a Sungrow smart meter, load power is the house's use. Without one it reads 0, so it (and grid power,
        // which is derived from it) is only reported when positive. Grid power is derived rather than read from the
        // meter register because neither source states that register's sign.
        if (S32(r, 5091) is > 0 and var load)
        {
            add(LoadPower, load, "W");
            if (ac is { } solar) add(GridPower, load - solar, "W"); // + import
            add(GridImportTodayMetric, U32(r, 5097) * 0.1, "kWh");
            add(GridExportTodayMetric, U32(r, 5093) * 0.1, "kWh");
        }
    }

    private static void DecodeHybrid(IReadOnlyDictionary<ushort, ushort> r, double? dcPower, int phases, double?[] phaseVolts,
        Action<string, double?, string> add)
    {
        add(PvPower, dcPower is { } dc ? Math.Max(0, dc) : null, "W");
        add(LoadPower, S32(r, 13008), "W");
        add(GridPower, -S32(r, 13010), "W"); // the register is export power: + selling

        // The grid voltage the inverter measures is only the grid's while it's connected; off grid it's the
        // inverter's own backup output, so the grid reads 0 V and SolPnl sees the outage.
        if (U16(r, 13030) is { } gridState and (OnGrid or OffGrid))
        {
            for (var i = 1; i <= phases; i++)
                add(ForPhase(GridVoltage, i), gridState == OnGrid ? phaseVolts[i - 1] : 0, "V");
        }

        if (U16(r, 13020) * 0.1 is >= 5.0 and var volts)
        {
            // Battery power and current are magnitudes on some firmware and signed on others; the power flow bits
            // say which way they go.
            var flow = (PowerFlow)(U16(r, 13001) ?? 0);
            double sign = flow.HasFlag(PowerFlow.BatteryCharging) ? 1 : flow.HasFlag(PowerFlow.BatteryDischarging) ? -1 : 0;
            add(BatteryVoltage, volts, "V");
            add(BatteryCurrent, S16(r, 13021) * 0.1 is { } a ? Math.Abs(a) * sign : null, "A");
            add(BatteryPower, S16(r, 13022) is { } w ? Math.Abs(w) * sign : null, "W");
            add(BatterySoc, U16(r, 13023) * 0.1, "%");
            add(BatteryTemperatureMetric, S16(r, 13025) * 0.1, "°C");
        }

        add(BatteryDischargeTodayMetric, U16(r, 13026) * 0.1, "kWh");
        add(GridImportTodayMetric, U16(r, 13036) * 0.1, "kWh");
    }

    /// <summary>Decodes the serial number (4990..4999, ASCII, two characters per register, high byte first).</summary>
    public static string? DecodeSerial(IReadOnlyDictionary<ushort, ushort> r)
    {
        var chars = new List<char>();
        for (ushort a = 4990; a < 5000; a++)
        {
            if (!r.TryGetValue(a, out var v)) return null;
            chars.Add((char)(v >> 8));
            chars.Add((char)(v & 0xFF));
        }
        var serial = new string(chars.TakeWhile(c => c != '\0').Where(c => c is > ' ' and <= '~').ToArray());
        return serial.Length > 0 ? serial : null;
    }

    private static double? U16(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        r.TryGetValue(a, out var v) && v != 0xFFFF ? v : null;

    private static double? S16(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        r.TryGetValue(a, out var v) && v is not (0x7FFF or 0x8000) ? (short)v : null;

    private static double? U32(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        Raw32(r, a) is { } v && v != 0xFFFFFFFF ? v : null;

    private static double? S32(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        Raw32(r, a) is { } v && v is not (0x7FFFFFFF or 0x80000000) ? (int)v : null;

    /// <summary>Low word first.</summary>
    private static uint? Raw32(IReadOnlyDictionary<ushort, ushort> r, ushort a) =>
        r.TryGetValue(a, out var lo) && r.TryGetValue((ushort)(a + 1), out var hi) ? (uint)hi << 16 | lo : null;
}

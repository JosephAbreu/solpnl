using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;
using static SolPnl.Plugin.SunSpec.SunSpecValues;

namespace SolPnl.Plugin.SunSpec;

/// <summary>Identity from SunSpec's common model (1).</summary>
public sealed record SunSpecIdentity(string? Manufacturer, string? Model, string? Version, string? SerialNumber);

/// <summary>
/// Decodes SunSpec information models into SolPnl metrics. Offsets follow the SunSpec Information Model
/// Specification: common (1), inverter (101–103 with scale factors, 111–113 floating point), multiple MPPT (160)
/// and meter (201–204 with scale factors, 211–214 floating point).
/// </summary>
public static class SunSpecDecoder
{
    public const ushort CommonModel = 1;
    public const ushort MpptModel = 160;

    public const string StateMetric = "vendor.sunspec.state";
    public const string AcPowerMetric = "vendor.sunspec.ac_power";
    public const string DcVoltageMetric = "vendor.sunspec.dc_voltage";
    public const string DcCurrentMetric = "vendor.sunspec.dc_current";
    public const string DcPowerMetric = "vendor.sunspec.dc_power";
    public const string AcEnergyMetric = "vendor.sunspec.energy.ac_total";
    public const string GridImportEnergyMetric = "vendor.sunspec.energy.grid_import_total";
    public const string GridExportEnergyMetric = "vendor.sunspec.energy.grid_export_total";

    /// <summary>Fault slots reported each poll (fault.code.1..4); further simultaneous events aren't reported.</summary>
    public const int FaultSlots = 4;

    public static bool IsInverter(ushort id) => id is >= 101 and <= 103 or >= 111 and <= 113;
    public static bool IsMeter(ushort id) => id is >= 201 and <= 204 or >= 211 and <= 214;

    /// <summary>Phases a model describes: 1 for single phase, 2 for split phase, 3 for three phase.</summary>
    public static int Phases(ushort id) => id switch
    {
        101 or 111 or 201 or 211 => 1,
        102 or 112 or 202 or 212 => 2,
        _ => 3,
    };

    public static SunSpecIdentity DecodeCommon(ushort[] r) =>
        new(Text(r, 0, 16), Text(r, 16, 16), Text(r, 40, 8), Text(r, 48, 16));

    /// <summary>Collects readings for one poll; also remembers values for derived totals.</summary>
    public sealed class Readings(string deviceId, DateTimeOffset timestamp)
    {
        private readonly List<Reading> _list = [];
        private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

        public IReadOnlyList<Reading> List => _list;

        public void Add(string metric, double? value, string unit)
        {
            if (value is not { } v) return;
            v = Math.Round(v, 3);
            _values[metric] = v;
            _list.Add(new Reading(deviceId, metric, v, unit, timestamp));
        }

        public double? Get(string metric) => _values.TryGetValue(metric, out var v) ? v : null;
    }

    // ---- Inverter (101-103, 111-113) --------------------------------------------------------------------------

    private sealed record InverterPoints(
        double? A, double?[] PhaseA, double?[] PhaseV, double? W, double? Hz, double? Wh,
        double? DcA, double? DcV, double? DcW, double?[] Temperatures, int? State, uint? Events);

    public static void DecodeInverter(ushort id, ushort[] r, Readings readings)
    {
        var p = id >= 111 ? FloatInverter(r) : IntegerInverter(r);
        var phases = Phases(id);

        readings.Add(StateMetric, p.State, "");

        for (var i = 1; i <= phases; i++)
        {
            readings.Add(ForPhase(InverterVoltage, i), p.PhaseV[i - 1], "V");
            readings.Add(ForPhase(InverterCurrent, i), p.PhaseA[i - 1], "A");
        }
        readings.Add(InverterFrequency, p.Hz, "Hz");

        // A grid-tied inverter's AC output is the solar power it delivers. Asleep (or off) at night, many inverters
        // report no AC power at all; that's 0 W of solar, not a missing reading.
        var asleep = p.State is SunSpecStates.Off or SunSpecStates.Sleeping or SunSpecStates.Standby;
        var acPower = p.W ?? (asleep ? 0 : null);
        readings.Add(AcPowerMetric, acPower, "W");
        readings.Add(PvPower, acPower is { } w ? Math.Max(0, w) : null, "W");

        readings.Add(DcVoltageMetric, p.DcV, "V");
        readings.Add(DcCurrentMetric, p.DcA, "A");
        readings.Add(DcPowerMetric, p.DcW, "W");
        readings.Add(AcEnergyMetric, p.Wh / 1000, "kWh");

        double? hottest = null;
        for (var i = 0; i < p.Temperatures.Length; i++)
        {
            // 0 °C exactly is how several inverters fill sensors they don't have.
            if (p.Temperatures[i] is not { } t || t == 0) continue;
            readings.Add(ForString(InverterTemperature, i + 1), t, "°C");
            hottest = Math.Max(hottest ?? double.MinValue, t);
        }
        readings.Add(InverterTemperature, hottest, "°C");

        if (p.Events is { } events)
        {
            var codes = SunSpecEvents.ActiveCodes(events);
            for (var slot = 1; slot <= FaultSlots; slot++)
                readings.Add(ForString(FaultCode, slot), slot <= codes.Count ? codes[slot - 1] : 0, "");
        }
    }

    private static InverterPoints IntegerInverter(ushort[] r)
    {
        int? aSf = Sf(r, 4), vSf = Sf(r, 11), wSf = Sf(r, 13), hzSf = Sf(r, 15), whSf = Sf(r, 24),
            dcaSf = Sf(r, 26), dcvSf = Sf(r, 28), dcwSf = Sf(r, 30), tSf = Sf(r, 35);
        return new InverterPoints(
            A: U16(r, 0, aSf),
            PhaseA: [U16(r, 1, aSf), U16(r, 2, aSf), U16(r, 3, aSf)],
            PhaseV: [U16(r, 8, vSf), U16(r, 9, vSf), U16(r, 10, vSf)],
            W: S16(r, 12, wSf),
            Hz: U16(r, 14, hzSf),
            Wh: Acc32(r, 22, whSf),
            DcA: U16(r, 25, dcaSf),
            DcV: U16(r, 27, dcvSf),
            DcW: S16(r, 29, dcwSf),
            Temperatures: [S16(r, 31, tSf), S16(r, 32, tSf), S16(r, 33, tSf), S16(r, 34, tSf)],
            State: Enum16(r, 36),
            Events: Bits32(r, 38));
    }

    private static InverterPoints FloatInverter(ushort[] r) => new(
        A: F32(r, 0),
        PhaseA: [F32(r, 2), F32(r, 4), F32(r, 6)],
        PhaseV: [F32(r, 14), F32(r, 16), F32(r, 18)],
        W: F32(r, 20),
        Hz: F32(r, 22),
        Wh: F32(r, 30) is > 0 and var wh ? wh : null,
        DcA: F32(r, 32),
        DcV: F32(r, 34),
        DcW: F32(r, 36),
        Temperatures: [F32(r, 38), F32(r, 40), F32(r, 42), F32(r, 44)],
        State: Enum16(r, 46),
        Events: Bits32(r, 48));

    // ---- Multiple MPPT (160) -----------------------------------------------------------------------------------

    private const int MpptFixedLength = 8;
    private const int MpptModuleLength = 20;

    /// <summary>One reading set per MPPT input ("pv.power.1", ...), numbered in the order the model lists them.</summary>
    public static void DecodeMppt(ushort[] r, Readings readings)
    {
        int? aSf = Sf(r, 0), vSf = Sf(r, 1), wSf = Sf(r, 2);
        var declared = Enum16(r, 6) ?? 0;
        var available = (r.Length - MpptFixedLength) / MpptModuleLength;
        var modules = Math.Min(declared, available);

        for (var i = 0; i < modules; i++)
        {
            var at = MpptFixedLength + i * MpptModuleLength;
            var a = U16(r, at + 9, aSf);
            var v = U16(r, at + 10, vSf);
            var w = U16(r, at + 11, wSf) ?? (a is { } aa && v is { } vv ? aa * vv : null);
            readings.Add(ForString(PvVoltage, i + 1), v, "V");
            readings.Add(ForString(PvCurrent, i + 1), a, "A");
            readings.Add(ForString(PvPower, i + 1), w, "W");
        }
    }

    // ---- Meter (201-204, 211-214) ------------------------------------------------------------------------------

    private sealed record MeterPoints(
        double?[] PhaseA, double?[] PhaseV, double? Hz, double? W, double?[] PhaseW, double? WhExport, double? WhImport);

    /// <param name="positiveIsExport">The meter reports power sold to the grid as positive (SolPnl's grid power is + import).</param>
    public static void DecodeMeter(ushort id, ushort[] r, bool positiveIsExport, Readings readings)
    {
        var p = id >= 211 ? FloatMeter(r) : IntegerMeter(r);
        var phases = Phases(id);
        var sign = positiveIsExport ? -1 : 1;

        for (var i = 1; i <= phases; i++)
        {
            readings.Add(ForPhase(GridVoltage, i), p.PhaseV[i - 1], "V");
            readings.Add(ForPhase(GridCurrent, i), p.PhaseA[i - 1], "A"); // as the meter reports it (often unsigned)
            readings.Add(ForPhase(GridPower, i), p.PhaseW[i - 1] * sign, "W");
        }
        readings.Add(GridFrequency, p.Hz, "Hz");
        readings.Add(GridPower, p.W * sign, "W");

        var (imported, exported) = positiveIsExport ? (p.WhExport, p.WhImport) : (p.WhImport, p.WhExport);
        readings.Add(GridImportEnergyMetric, imported / 1000, "kWh");
        readings.Add(GridExportEnergyMetric, exported / 1000, "kWh");
    }

    private static MeterPoints IntegerMeter(ushort[] r)
    {
        int? aSf = Sf(r, 4), vSf = Sf(r, 13), hzSf = Sf(r, 15), wSf = Sf(r, 20), whSf = Sf(r, 52);
        return new MeterPoints(
            PhaseA: [S16(r, 1, aSf), S16(r, 2, aSf), S16(r, 3, aSf)],
            PhaseV: [S16(r, 6, vSf), S16(r, 7, vSf), S16(r, 8, vSf)],
            Hz: S16(r, 14, hzSf),
            W: S16(r, 16, wSf),
            PhaseW: [S16(r, 17, wSf), S16(r, 18, wSf), S16(r, 19, wSf)],
            WhExport: Acc32(r, 36, whSf),
            WhImport: Acc32(r, 44, whSf));
    }

    private static MeterPoints FloatMeter(ushort[] r) => new(
        PhaseA: [F32(r, 2), F32(r, 4), F32(r, 6)],
        PhaseV: [F32(r, 10), F32(r, 12), F32(r, 14)],
        Hz: F32(r, 24),
        W: F32(r, 26),
        PhaseW: [F32(r, 28), F32(r, 30), F32(r, 32)],
        WhExport: F32(r, 58) is > 0 and var e ? e : null,
        WhImport: F32(r, 66) is > 0 and var i ? i : null);

    // ---- Derived -----------------------------------------------------------------------------------------------

    /// <summary>
    /// With a grid meter at the connection point, the house uses what the inverter delivers plus what comes from the
    /// grid (negative when exporting).
    /// </summary>
    public static void AddLoad(Readings readings)
    {
        if (readings.Get(AcPowerMetric) is { } ac && readings.Get(GridPower) is { } grid)
            readings.Add(LoadPower, Math.Max(0, Math.Max(0, ac) + grid), "W");
    }
}

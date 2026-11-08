using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.SolarmanSrne;

/// <summary>
/// Register map and decoder for SRNE-family split-phase inverters (e.g. Sungoldpower SPH10048P).
/// Verified 2026-11-07 against a live unit in no-battery mode: decoded PV and load values matched the
/// inverter's own display. Entries marked "unverified" follow the SRNE layout but haven't been checked
/// against a live value (e.g. battery registers, which read 0 without a battery).
/// </summary>
public static class SrneRegisterMap
{
    /// <summary>
    /// Blocks read each poll. Every address in a block must exist (otherwise the inverter returns
    /// exception 0x02), and blocks must be 32 registers or fewer (larger reads fail with exception 0x0A).
    /// </summary>
    public static readonly IReadOnlyList<(ushort Start, ushort Count)> Blocks =
    [
        (0x0100, 18), // battery + PV: 0x0100..0x0111
        (0x0200, 32), // clock, state, grid/inverter/load L1: 0x0200..0x021F
        (0x0220, 32), // temperatures, L2: 0x0220..0x023F
    ];

    /// <summary>Below this battery voltage we treat the battery as absent (no-battery mode reads ~0 V).</summary>
    public const double BatteryPresentMinVoltage = 10.0;

    public const string StateMetric = "vendor.srne.state";
    public const string DcBusVoltageMetric = "vendor.srne.dc_bus_voltage";

    private sealed record Register(ushort Address, string Metric, double Scale, string Unit, bool Signed = false);

    private static readonly Register[] Battery =
    [
        new(0x0100, BatterySoc, 1, "%"),            // unverified
        new(0x0101, BatteryVoltage, 0.1, "V"),      // unverified
        new(0x0102, BatteryCurrent, 0.1, "A", Signed: true), // unverified, including sign convention
    ];

    private static readonly Register[] Common =
    [
        new(0x0107, ForString(PvVoltage, 1), 0.1, "V"),
        new(0x0108, ForString(PvCurrent, 1), 0.1, "A"),
        new(0x0109, ForString(PvPower, 1), 1, "W"),
        new(0x010F, ForString(PvVoltage, 2), 0.1, "V"),
        new(0x0110, ForString(PvCurrent, 2), 0.1, "A"),
        new(0x0111, ForString(PvPower, 2), 1, "W"),

        // Current fault codes 1-4 (0 = none), after the fault bit field 0x0200..0x0203. All 0 on the live unit with
        // no fault; non-zero codes follow SRNE's fault table (see SrneFaults) and haven't been seen live yet.
        new(0x0204, ForString(FaultCode, 1), 1, ""),
        new(0x0205, ForString(FaultCode, 2), 1, ""),
        new(0x0206, ForString(FaultCode, 3), 1, ""),
        new(0x0207, ForString(FaultCode, 4), 1, ""),
        new(0x0210, StateMetric, 1, ""),            // operating state; meaning of values not yet mapped

        new(0x0212, DcBusVoltageMetric, 0.1, "V"),         // internal DC bus; 0x0228 + 0x0229 are its two halves
        new(0x0213, ForPhase(GridVoltage, 1), 0.1, "V"),
        new(0x0214, ForPhase(GridCurrent, 1), 0.1, "A"),
        new(0x0215, GridFrequency, 0.01, "Hz"),
        new(0x0216, ForPhase(InverterVoltage, 1), 0.1, "V"),
        new(0x0217, ForPhase(InverterCurrent, 1), 0.1, "A"),
        new(0x0218, InverterFrequency, 0.01, "Hz"),
        new(0x0219, ForPhase(LoadCurrent, 1), 0.1, "A"),
        new(0x021B, ForPhase(LoadPower, 1), 1, "W"),
        new(0x021C, ForPhase(LoadApparentPower, 1), 1, "VA"),
        new(0x021F, ForPhase(LoadPercent, 1), 1, "%"),

        new(0x0220, ForString(InverterTemperature, 1), 0.1, "°C", Signed: true),
        new(0x0221, ForString(InverterTemperature, 2), 0.1, "°C", Signed: true),
        new(0x0222, ForString(InverterTemperature, 3), 0.1, "°C", Signed: true),

        new(0x022A, ForPhase(GridVoltage, 2), 0.1, "V"),
        new(0x022C, ForPhase(InverterVoltage, 2), 0.1, "V"),
        new(0x022E, ForPhase(InverterCurrent, 2), 0.1, "A"),
        new(0x0230, ForPhase(LoadCurrent, 2), 0.1, "A"),
        new(0x0232, ForPhase(LoadPower, 2), 1, "W"),
        new(0x0234, ForPhase(LoadApparentPower, 2), 1, "VA"),
        new(0x0236, ForPhase(LoadPercent, 2), 1, "%"),

        // Grid side. Verified 2026-11-07: at night (grid only) L2 current/VA matched the L2 load exactly, and day and
        // night energy balances hold (solar + grid = load + inverter losses). L1 power is signed; small negative values
        // near zero load are measurement noise (export is unverified).
        new(0x0238, ForPhase(GridCurrent, 2), 0.1, "A"),
        new(0x023A, ForPhase(GridPower, 1), 1, "W", Signed: true),
        new(0x023B, ForPhase(GridPower, 2), 1, "W", Signed: true),
        new(0x023D, ForPhase(GridApparentPower, 1), 1, "VA"),
        new(0x023E, ForPhase(GridApparentPower, 2), 1, "VA"),
    ];

    public static IReadOnlyList<Reading> Decode(
        IReadOnlyDictionary<ushort, ushort> registers, string deviceId, DateTimeOffset timestamp)
    {
        var readings = new List<Reading>();
        var values = new Dictionary<string, double>();

        void Add(string metric, double value, string unit)
        {
            value = Math.Round(value, 3);
            values[metric] = value;
            readings.Add(new Reading(deviceId, metric, value, unit, timestamp));
        }

        void AddRegisters(IEnumerable<Register> map)
        {
            foreach (var r in map)
            {
                if (!registers.TryGetValue(r.Address, out var raw)) continue;
                Add(r.Metric, (r.Signed ? (short)raw : raw) * r.Scale, r.Unit);
            }
        }

        AddRegisters(Common);

        if (registers.TryGetValue(0x0101, out var batteryRaw) && batteryRaw * 0.1 >= BatteryPresentMinVoltage)
            AddRegisters(Battery);

        // Derived totals
        if (values.TryGetValue(ForString(PvPower, 1), out var pv1) && values.TryGetValue(ForString(PvPower, 2), out var pv2))
            Add(PvPower, pv1 + pv2, "W");

        if (values.TryGetValue(ForPhase(LoadPower, 1), out var l1) && values.TryGetValue(ForPhase(LoadPower, 2), out var l2))
            Add(LoadPower, l1 + l2, "W");

        if (values.TryGetValue(ForPhase(LoadApparentPower, 1), out var s1) && values.TryGetValue(ForPhase(LoadApparentPower, 2), out var s2))
            Add(LoadApparentPower, s1 + s2, "VA");

        if (values.TryGetValue(ForPhase(GridPower, 1), out var g1) && values.TryGetValue(ForPhase(GridPower, 2), out var g2))
            Add(GridPower, g1 + g2, "W");

        if (values.TryGetValue(ForPhase(GridApparentPower, 1), out var gs1) && values.TryGetValue(ForPhase(GridApparentPower, 2), out var gs2))
            Add(GridApparentPower, gs1 + gs2, "VA");

        var temperatures = Enumerable.Range(1, 3)
            .Select(i => values.TryGetValue(ForString(InverterTemperature, i), out var t) ? t : (double?)null)
            .OfType<double>()
            .ToList();
        if (temperatures.Count > 0)
            Add(InverterTemperature, temperatures.Max(), "°C");

        return readings;
    }

    /// <summary>Decodes the inverter clock (0x020C..0x020E: YY MM | DD hh | mm ss). The clock has no time zone.</summary>
    public static DateTime? DecodeClock(IReadOnlyDictionary<ushort, ushort> registers)
    {
        if (!registers.TryGetValue(0x020C, out var ym) ||
            !registers.TryGetValue(0x020D, out var dh) ||
            !registers.TryGetValue(0x020E, out var ms))
            return null;

        try
        {
            return new DateTime(2000 + (ym >> 8), ym & 0xFF, dh >> 8, dh & 0xFF, ms >> 8, ms & 0xFF, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

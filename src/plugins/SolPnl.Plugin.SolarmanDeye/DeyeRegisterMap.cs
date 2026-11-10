using System.Text;
using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.SolarmanDeye;

/// <summary>The inverter's own serial number and rating, from its device information registers.</summary>
public sealed record DeyeIdentity(string? SerialNumber, double? RatedPowerWatts, int? MpptCount);

/// <summary>
/// Register map and decoder for Deye single-phase and split-phase hybrid inverters (SUN-xK-SG0xLP1, including the
/// -US models) and rebrands such as Sunsynk. Addresses are decimal holding registers, as in Deye's Modbus protocol.
/// </summary>
/// <remarks>
/// Not yet verified against a live inverter. The addresses and scales are those used by two independent open-source
/// readers (kellerza/sunsynk and ha-solarman's deye_hybrid definition), which agree on them. Neither states the sign
/// conventions; the ones used here are noted at each register and need checking on a live unit.
/// </remarks>
public static class DeyeRegisterMap
{
    /// <summary>
    /// Blocks read each poll, kept to 32 registers or fewer because some loggers and inverters reject larger reads.
    /// </summary>
    public static readonly IReadOnlyList<(ushort Start, ushort Count)> Blocks =
    [
        (0, 25),   // device information, serial number, rated power, clock: 0..24
        (59, 30),  // state, energy counters: 59..88
        (89, 28),  // temperatures, total PV energy, faults, PV voltage/current: 89..116
        (150, 30), // grid, inverter output, load: 150..179
        (180, 17), // load current, battery, PV power, frequencies: 180..196
    ];

    /// <summary>Below this battery voltage we treat the battery as absent.</summary>
    public const double BatteryPresentMinVoltage = 10.0;

    /// <summary>Most of these inverters have two MPPTs; used when register 18 doesn't say.</summary>
    public const int DefaultMpptCount = 2;

    public const string StateMetric = "vendor.deye.state";
    public const string BatteryTemperatureMetric = "vendor.deye.battery_temperature";
    public const string GenPowerMetric = "vendor.deye.gen_power";

    private const string Energy = "vendor.deye.energy.";

    private enum Kind { Unsigned, Signed, Temperature }

    private sealed record Register(ushort Address, string Metric, double Scale, string Unit, Kind Kind = Kind.Unsigned, bool Negate = false);

    /// <summary>A 32-bit counter in two registers, low word first.</summary>
    private sealed record Counter(ushort Low, ushort High, string Metric);

    private static readonly Register[] Common =
    [
        new(59, StateMetric, 1, ""), // see DeyeStates

        new(79, GridFrequency, 0.01, "Hz"),
        new(150, ForPhase(GridVoltage, 1), 0.1, "V"),
        new(160, ForPhase(GridCurrent, 1), 0.01, "A", Kind.Signed),
        // Grid power: + import / - export assumed (as Deye's app shows it); unverified.
        new(167, ForPhase(GridPower, 1), 1, "W", Kind.Signed),
        new(169, GridPower, 1, "W", Kind.Signed),

        new(154, ForPhase(InverterVoltage, 1), 0.1, "V"),
        new(164, ForPhase(InverterCurrent, 1), 0.01, "A", Kind.Signed),
        new(193, InverterFrequency, 0.01, "Hz"),

        new(176, ForPhase(LoadPower, 1), 1, "W", Kind.Signed),
        new(178, LoadPower, 1, "W", Kind.Signed),
        new(179, ForPhase(LoadCurrent, 1), 0.01, "A", Kind.Signed),

        // Temperatures are stored as (°C × 10) + 1000.
        new(90, ForString(InverterTemperature, 1), 0.1, "°C", Kind.Temperature), // DC transformer
        new(91, ForString(InverterTemperature, 2), 0.1, "°C", Kind.Temperature), // heat sink

        new(166, GenPowerMetric, 1, "W", Kind.Signed), // GEN / AUX port (generator, smart load or microinverter)
    ];

    private static readonly Register[] SecondLeg =
    [
        new(151, ForPhase(GridVoltage, 2), 0.1, "V"),
        new(161, ForPhase(GridCurrent, 2), 0.01, "A", Kind.Signed),
        new(168, ForPhase(GridPower, 2), 1, "W", Kind.Signed),
        new(155, ForPhase(InverterVoltage, 2), 0.1, "V"),
        new(165, ForPhase(InverterCurrent, 2), 0.01, "A", Kind.Signed),
        new(177, ForPhase(LoadPower, 2), 1, "W", Kind.Signed),
        new(180, ForPhase(LoadCurrent, 2), 0.01, "A", Kind.Signed),
    ];

    private static readonly Register[] Battery =
    [
        new(184, BatterySoc, 1, "%"),
        new(183, BatteryVoltage, 0.01, "V"),
        // Deye reports battery power and current + discharging / - charging; SolPnl's metrics are the other way round.
        // Unverified on a live unit.
        new(190, BatteryPower, 1, "W", Kind.Signed, Negate: true),
        new(191, BatteryCurrent, 0.01, "A", Kind.Signed, Negate: true),
        new(182, BatteryTemperatureMetric, 0.1, "°C", Kind.Temperature),
    ];

    private static readonly Register[] DailyEnergy =
    [
        new(108, Energy + "pv_today", 0.1, "kWh"),
        new(84, Energy + "load_today", 0.1, "kWh"),
        new(76, Energy + "grid_import_today", 0.1, "kWh"),
        new(77, Energy + "grid_export_today", 0.1, "kWh"),
        new(70, Energy + "battery_charge_today", 0.1, "kWh"),
        new(71, Energy + "battery_discharge_today", 0.1, "kWh"),
    ];

    private static readonly Counter[] TotalEnergy =
    [
        new(96, 97, Energy + "pv_total"),
        new(85, 86, Energy + "load_total"),
        new(78, 80, Energy + "grid_import_total"), // the high word is 80; 79 is grid frequency
        new(81, 82, Energy + "grid_export_total"),
        new(72, 73, Energy + "battery_charge_total"),
        new(74, 75, Energy + "battery_discharge_total"),
    ];

    /// <summary>The four fault registers hold a 64-bit field, 103 first: bit n (0-based) set means fault F(n+1).</summary>
    private const ushort FirstFaultRegister = 103;

    /// <summary>Fault slots reported each poll (fault.code.1..4); further simultaneous faults aren't reported.</summary>
    public const int FaultSlots = 4;

    public static IReadOnlyList<Reading> Decode(
        IReadOnlyDictionary<ushort, ushort> registers, string deviceId, DateTimeOffset timestamp, bool splitPhase)
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
                var value = r.Kind switch
                {
                    Kind.Signed => (short)raw * r.Scale,
                    Kind.Temperature => (raw - 1000) * r.Scale,
                    _ => raw * r.Scale,
                };
                Add(r.Metric, r.Negate ? -value : value, r.Unit);
            }
        }

        AddRegisters(Common);
        if (splitPhase) AddRegisters(SecondLeg);

        if (registers.TryGetValue(183, out var batteryRaw) && batteryRaw * 0.01 >= BatteryPresentMinVoltage)
            AddRegisters(Battery);

        // PV inputs, as many as the inverter has MPPTs.
        var mppts = DecodeIdentity(registers).MpptCount ?? DefaultMpptCount;
        double? pvTotal = 0;
        for (var i = 1; i <= mppts; i++)
        {
            var at = (ushort)(109 + (i - 1) * 2);
            if (registers.TryGetValue(at, out var v)) Add(ForString(PvVoltage, i), v * 0.1, "V");
            if (registers.TryGetValue((ushort)(at + 1), out var a)) Add(ForString(PvCurrent, i), a * 0.1, "A");
            if (registers.TryGetValue((ushort)(185 + i), out var w))
            {
                Add(ForString(PvPower, i), w, "W");
                pvTotal += w;
            }
            else pvTotal = null;
        }
        if (pvTotal is { } pv) Add(PvPower, pv, "W");

        var temperatures = Enumerable.Range(1, 2)
            .Select(i => values.TryGetValue(ForString(InverterTemperature, i), out var t) ? t : (double?)null)
            .OfType<double>()
            .ToList();
        if (temperatures.Count > 0)
            Add(InverterTemperature, temperatures.Max(), "°C");

        if (TryReadFaults(registers, out var faults))
        {
            for (var slot = 1; slot <= FaultSlots; slot++)
                Add(ForString(FaultCode, slot), slot <= faults.Count ? faults[slot - 1] : 0, "");
        }

        AddRegisters(DailyEnergy);
        foreach (var c in TotalEnergy)
        {
            if (registers.TryGetValue(c.Low, out var lo) && registers.TryGetValue(c.High, out var hi))
                Add(c.Metric, ((uint)hi << 16 | lo) * 0.1, "kWh");
        }

        return readings;
    }

    /// <summary>Active fault codes (F numbers), lowest first.</summary>
    public static bool TryReadFaults(IReadOnlyDictionary<ushort, ushort> registers, out List<int> codes)
    {
        codes = [];
        for (var word = 0; word < 4; word++)
        {
            if (!registers.TryGetValue((ushort)(FirstFaultRegister + word), out var bits)) return false;
            for (var bit = 0; bit < 16; bit++)
                if ((bits & (1 << bit)) != 0) codes.Add(word * 16 + bit + 1);
        }
        return true;
    }

    /// <summary>Serial number (ASCII in 3..7, two characters per register, high byte first), rated power and MPPT count.</summary>
    public static DeyeIdentity DecodeIdentity(IReadOnlyDictionary<ushort, ushort> registers)
    {
        string? serial = null;
        var chars = new StringBuilder();
        for (ushort a = 3; a <= 7; a++)
        {
            if (!registers.TryGetValue(a, out var r)) { chars.Clear(); break; }
            foreach (var b in new[] { (byte)(r >> 8), (byte)r })
                if (b is >= 0x21 and <= 0x7E) chars.Append((char)b);
        }
        if (chars.Length > 0) serial = chars.ToString();

        double? rated = registers.TryGetValue(16, out var lo) && registers.TryGetValue(17, out var hi)
            ? ((uint)hi << 16 | lo) * 0.1
            : null;

        int? mppts = registers.TryGetValue(18, out var r18) && ((r18 >> 8) & 0x0F) is var n and >= 1 and <= 4 ? n : null;

        return new DeyeIdentity(serial, rated, mppts);
    }

    /// <summary>Decodes the inverter clock (22..24: YY MM | DD hh | mm ss). The clock has no time zone.</summary>
    public static DateTime? DecodeClock(IReadOnlyDictionary<ushort, ushort> registers)
    {
        if (!registers.TryGetValue(22, out var ym) ||
            !registers.TryGetValue(23, out var dh) ||
            !registers.TryGetValue(24, out var ms))
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

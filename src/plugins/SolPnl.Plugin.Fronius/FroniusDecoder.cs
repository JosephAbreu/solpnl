using System.Text.Json;
using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.Fronius;

/// <summary>
/// Decodes Fronius Solar API v1 responses (JSON: Head.Status, Body.Data). Key names follow pyfronius, the client
/// behind Home Assistant's Fronius integration, and its captured responses.
/// </summary>
/// <remarks>
/// Power flow signs (from a captured night-time response, where P_Grid = +367.7 W and P_Load = -367.7 W): P_Grid is
/// + from the grid, P_Load is negative while the house uses power. P_Akku is taken as + discharging (Fronius'
/// convention; not in pyfronius, so unverified).
/// </remarks>
public static class FroniusDecoder
{
    public const string StateMetric = "vendor.fronius.status";
    public const string AcPowerMetric = "vendor.fronius.ac_power";
    public const string PvEnergyTodayMetric = "vendor.fronius.energy.pv_today";
    public const string PvEnergyTotalMetric = "vendor.fronius.energy.pv_total";
    public const string GridImportTotalMetric = "vendor.fronius.energy.grid_import_total";
    public const string GridExportTotalMetric = "vendor.fronius.energy.grid_export_total";
    public const string BatteryTemperatureMetric = "vendor.fronius.battery_temperature";

    /// <summary>Head.Status.Code for a device that isn't available (e.g. an inverter asleep at night).</summary>
    public const int DeviceNotAvailable = 12;

    /// <summary>DeviceStatus.StatusCode for "Error".</summary>
    public const int StatusError = 10;

    public const int FaultSlots = 4;

    /// <summary>Thrown when the API answers with a status other than OK.</summary>
    public sealed class StatusException(int code, string? reason)
        : Exception($"Fronius Solar API status {code}{(string.IsNullOrEmpty(reason) ? "" : $": {reason}")}")
    {
        public int Code { get; } = code;
    }

    /// <summary>Returns Body.Data, or throws <see cref="StatusException"/> for a non-OK status.</summary>
    public static JsonElement Data(JsonDocument response)
    {
        var root = response.RootElement;
        if (root.TryGetProperty("Head", out var head) && head.TryGetProperty("Status", out var status) &&
            status.TryGetProperty("Code", out var code) && code.TryGetInt32(out var c) && c != 0)
            throw new StatusException(c, status.TryGetProperty("Reason", out var r) ? r.GetString() : null);
        return root.GetProperty("Body").GetProperty("Data");
    }

    public sealed class Readings(string deviceId, DateTimeOffset timestamp)
    {
        private readonly List<Reading> _list = [];
        public IReadOnlyList<Reading> List => _list;

        public void Add(string metric, double? value, string unit)
        {
            if (value is { } v && double.IsFinite(v))
                _list.Add(new Reading(deviceId, metric, Math.Round(v, 3), unit, timestamp));
        }

        public bool Has(string metric) => _list.Any(r => r.Metric == metric);
    }

    // ---- GetPowerFlowRealtimeData.fcgi ----

    public static void DecodePowerFlow(JsonElement data, string inverterId, Readings readings)
    {
        if (!data.TryGetProperty("Site", out var site)) return;

        // P_PV is null while the inverter sleeps: that's no solar, not a missing reading.
        readings.Add(PvPower, Math.Max(0, Number(site, "P_PV") ?? 0), "W");
        readings.Add(GridPower, Number(site, "P_Grid"), "W");                       // null without a meter
        readings.Add(LoadPower, -Number(site, "P_Load") is { } load ? Math.Max(0, load) : null, "W");
        readings.Add(BatteryPower, -Number(site, "P_Akku"), "W");                   // + charging

        if (data.TryGetProperty("Inverters", out var inverters) && inverters.ValueKind == JsonValueKind.Object &&
            inverters.TryGetProperty(inverterId, out var inverter))
            readings.Add(BatterySoc, Number(inverter, "SOC"), "%");
    }

    // ---- GetInverterRealtimeData.cgi (CommonInverterData, 3PInverterData) ----

    public static void DecodeInverter(JsonElement common, JsonElement? threePhase, Readings readings)
    {
        readings.Add(AcPowerMetric, Value(common, "PAC"), "W");
        readings.Add(InverterFrequency, Value(common, "FAC"), "Hz");
        readings.Add(PvEnergyTodayMetric, Value(common, "DAY_ENERGY") / 1000, "kWh");
        readings.Add(PvEnergyTotalMetric, Value(common, "TOTAL_ENERGY") / 1000, "kWh");

        if (threePhase is { } p3 && Value(p3, "UAC_L1") is not null)
        {
            for (var i = 1; i <= 3; i++)
            {
                readings.Add(ForPhase(InverterVoltage, i), Value(p3, $"UAC_L{i}"), "V");
                readings.Add(ForPhase(InverterCurrent, i), Value(p3, $"IAC_L{i}"), "A");
            }
        }
        else
        {
            readings.Add(ForPhase(InverterVoltage, 1), Value(common, "UAC"), "V");
            readings.Add(ForPhase(InverterCurrent, 1), Value(common, "IAC"), "A");
        }

        // DC inputs: UDC/IDC, then UDC_2/IDC_2 ... (GEN24 and newer firmware report each MPPT).
        for (var i = 1; i <= 9; i++)
        {
            var suffix = i == 1 ? "" : $"_{i}";
            var v = Value(common, "UDC" + suffix);
            var a = Value(common, "IDC" + suffix);
            if (v is null && a is null) { if (i > 1) break; continue; }
            readings.Add(ForString(PvVoltage, i), v, "V");
            readings.Add(ForString(PvCurrent, i), a, "A");
            readings.Add(ForString(PvPower, i), v * a, "W");
        }

        if (common.TryGetProperty("DeviceStatus", out var status))
        {
            var statusCode = Number(status, "StatusCode");
            readings.Add(StateMetric, statusCode, "");
            // ErrorCode stays set in normal states (e.g. at dusk while the inverter powers down), so it's only a fault
            // while the inverter is in its Error state.
            if (Number(status, "ErrorCode") is { } error)
            {
                readings.Add(ForString(FaultCode, 1), statusCode == StatusError ? error : 0, "");
                for (var slot = 2; slot <= FaultSlots; slot++) readings.Add(ForString(FaultCode, slot), 0, "");
            }
        }
    }

    // ---- GetMeterRealtimeData.cgi?Scope=System ----

    /// <param name="phases">The meter's phase count, learned on the first read (unused phases report nothing or 0).</param>
    /// <returns>The phase count to remember.</returns>
    public static int DecodeMeter(JsonElement data, int? phases, Readings readings)
    {
        var meter = FirstDevice(data);
        if (meter is not { } m) return phases ?? 0;

        var count = phases ?? Enumerable.Range(1, 3).Count(i => Number(m, $"Voltage_AC_Phase_{i}") is > 0);
        // The meter is powered by the grid, so its voltage is the grid's even while the inverter sleeps: 0 V in an
        // outage. Grid power comes from the power flow, which accounts for where the meter is installed.
        for (var i = 1; i <= count; i++)
        {
            readings.Add(ForPhase(GridVoltage, i), Number(m, $"Voltage_AC_Phase_{i}") ?? 0, "V");
            readings.Add(ForPhase(GridCurrent, i), Number(m, $"Current_AC_Phase_{i}"), "A");
        }
        readings.Add(GridFrequency, Number(m, "Frequency_Phase_Average"), "Hz");

        // At the grid connection point (location 0), "consumed" is what came from the grid.
        if (Number(m, "Meter_Location_Current") == 0)
        {
            readings.Add(GridImportTotalMetric,
                (Number(m, "EnergyReal_WAC_Sum_Consumed") ?? Number(m, "SMARTMETER_ENERGYACTIVE_CONSUMED_SUM_F64")) / 1000, "kWh");
            readings.Add(GridExportTotalMetric,
                (Number(m, "EnergyReal_WAC_Sum_Produced") ?? Number(m, "SMARTMETER_ENERGYACTIVE_PRODUCED_SUM_F64")) / 1000, "kWh");
        }
        return count;
    }

    // ---- GetStorageRealtimeData.cgi?Scope=System ----

    public static void DecodeStorage(JsonElement data, Readings readings)
    {
        if (FirstDevice(data) is not { } storage || !storage.TryGetProperty("Controller", out var c)) return;
        if (!readings.Has(BatterySoc)) readings.Add(BatterySoc, Number(c, "StateOfCharge_Relative"), "%");
        if (Number(c, "Voltage_DC") is > 0 and var volts) readings.Add(BatteryVoltage, volts, "V");
        readings.Add(BatteryTemperatureMetric, Number(c, "Temperature_Cell"), "°C");
    }

    /// <summary>Fills battery current from power and voltage (the API's current sign isn't documented).</summary>
    public static void AddBatteryCurrent(Readings readings)
    {
        var list = readings.List;
        var power = list.FirstOrDefault(r => r.Metric == BatteryPower)?.Value;
        var volts = list.FirstOrDefault(r => r.Metric == BatteryVoltage)?.Value;
        if (power is { } p && volts is > 1) readings.Add(BatteryCurrent, p / volts, "A");
    }

    // ---- JSON helpers ----

    /// <summary>The first device in a Scope=System answer ({"0": {...}}), or null.</summary>
    private static JsonElement? FirstDevice(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object ? data.EnumerateObject().Select(p => p.Value)
            .Where(v => v.ValueKind == JsonValueKind.Object).Cast<JsonElement?>().FirstOrDefault() : null;

    /// <summary>A plain number property; null when absent or null.</summary>
    public static double? Number(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    /// <summary>A {"Value": x, "Unit": u} property's value.</summary>
    public static double? Value(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p) ? Number(p, "Value") : null;
}

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SolPnl.Contracts.Models;
using static SolPnl.Contracts.Models.Metrics;

namespace SolPnl.Plugin.Enphase;

/// <summary>The gateway's identity from /info (XML, no authentication needed).</summary>
public sealed record EnphaseInfo(string? SerialNumber, string? PartNumber, string? Firmware, bool Metered);

/// <summary>
/// Decodes the Enphase IQ Gateway's local API, following pyenphase (the library behind Home Assistant's Enphase
/// integration) and its captured responses.
/// </summary>
/// <remarks>
/// Signs: a CT meter's activePower is "positive delivering, negative receiving" (pyenphase); the net-consumption
/// (grid) meter reads +522 W in a captured response while the house uses ~523 W and solar is ~0, so + is import.
/// Battery power's sign isn't in pyenphase and is taken as + discharging (Enphase's convention); unverified.
/// </remarks>
public static class EnphaseDecoder
{
    public const string TokenDaysLeftMetric = "vendor.enphase.token_days_left";
    public const string MicroinvertersMetric = "vendor.enphase.microinverters_producing";
    public const string PvEnergyTodayMetric = "vendor.enphase.energy.pv_today";
    public const string PvEnergyTotalMetric = "vendor.enphase.energy.pv_total";
    public const string GridImportTotalMetric = "vendor.enphase.energy.grid_import_total";
    public const string GridExportTotalMetric = "vendor.enphase.energy.grid_export_total";

    public static EnphaseInfo ParseInfo(string xml)
    {
        var device = XDocument.Parse(xml).Root?.Element("device");
        return new EnphaseInfo(
            device?.Element("sn")?.Value.Trim(),
            device?.Element("pn")?.Value.Trim(),
            device?.Element("software")?.Value.Trim(),
            string.Equals(device?.Element("imeter")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The token's expiry (the JWT's "exp" claim), without checking its signature; null if unreadable.</summary>
    public static DateTimeOffset? TokenExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public sealed class Readings(string deviceId, DateTimeOffset timestamp)
    {
        private readonly List<Reading> _list = [];
        private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
        public IReadOnlyList<Reading> List => _list;
        public DateTimeOffset Timestamp => timestamp;

        public void Add(string metric, double? value, string unit)
        {
            if (value is not { } v || !double.IsFinite(v)) return;
            v = Math.Round(v, 3);
            _values[metric] = v;
            _list.Add(new Reading(deviceId, metric, v, unit, timestamp));
        }

        public double? Get(string metric) => _values.TryGetValue(metric, out var v) ? v : null;
    }

    // ---- /production.json?details=1 ----

    public static void DecodeProduction(JsonElement root, Readings readings)
    {
        // Solar: the production CT ("eim") when it's active, otherwise the microinverters' own reports.
        JsonElement? eim = null, inverters = null;
        foreach (var e in Array(root, "production"))
        {
            if (Str(e, "type") == "eim" && Str(e, "measurementType") == "production") eim = e;
            else if (Str(e, "type") == "inverters") inverters = e;
        }
        var source = eim is { } m && Num(m, "activeCount") is > 0 ? m : inverters;
        if (source is { } s)
        {
            // A production CT reads slightly negative at night (the gateway's own draw): no solar.
            readings.Add(PvPower, Num(s, "wNow") is { } w ? Math.Max(0, w) : null, "W");
            readings.Add(PvEnergyTodayMetric, Num(s, "whToday") / 1000, "kWh");
            readings.Add(PvEnergyTotalMetric, Num(s, "whLifetime") / 1000, "kWh");
        }
        if (inverters is { } inv) readings.Add(MicroinvertersMetric, Num(inv, "activeCount"), "");

        // Consumption CTs: "net-consumption" is the grid (+ import); "total-consumption" is the house.
        double? net = null, total = null;
        foreach (var e in Array(root, "consumption"))
        {
            if (Str(e, "type") != "eim" || Num(e, "activeCount") is not > 0) continue;
            switch (Str(e, "measurementType"))
            {
                case "net-consumption": net = Num(e, "wNow"); break;
                case "total-consumption": total = Num(e, "wNow"); break;
            }
        }
        readings.Add(GridPower, net, "W");

        // Some firmware reports total equal to net; pyenphase repairs that as net + production, and so does this.
        var solar = readings.Get(PvPower);
        if (total is { } t && net is { } n && solar is > 0 && Math.Abs(t - n) < 0.5) total = n + solar;
        total ??= net is { } nn && solar is { } pv ? nn + pv : null;
        readings.Add(LoadPower, total is { } load ? Math.Max(0, load) : null, "W");
    }

    // ---- /ivp/meters + /ivp/meters/readings ----

    /// <summary>
    /// Grid voltage, current and frequency from the CT meters. The gateway is powered by the grid, so its meters keep
    /// measuring at night (unlike the microinverters) and read 0 V in an outage.
    /// </summary>
    public static void DecodeMeters(JsonElement meters, JsonElement meterReadings, Readings readings)
    {
        var enabled = new Dictionary<long, (string Type, int Phases)>();
        foreach (var m in Items(meters))
        {
            if (Str(m, "state") != "enabled" || Num(m, "eid") is not { } eid) continue;
            enabled[(long)eid] = (Str(m, "measurementType") ?? "", (int)(Num(m, "phaseCount") ?? 1));
        }

        // Prefer the grid (net-consumption) meter, then any other enabled one.
        JsonElement? best = null;
        var bestPhases = 1;
        var bestIsGrid = false;
        foreach (var r in Items(meterReadings))
        {
            if (Num(r, "eid") is not { } eid || !enabled.TryGetValue((long)eid, out var meta)) continue;
            var isGrid = meta.Type == "net-consumption";
            if (best is null || (isGrid && !bestIsGrid)) (best, bestPhases, bestIsGrid) = (r, meta.Phases, isGrid);
        }
        if (best is not { } reading) return;

        var channels = Array(reading, "channels").ToList();
        if (bestPhases > 1 && channels.Count >= bestPhases)
        {
            for (var i = 1; i <= bestPhases; i++)
            {
                readings.Add(ForPhase(GridVoltage, i), Num(channels[i - 1], "voltage"), "V");
                if (bestIsGrid) readings.Add(ForPhase(GridPower, i), Num(channels[i - 1], "activePower"), "W");
            }
        }
        else
            readings.Add(ForPhase(GridVoltage, 1), Num(reading, "voltage"), "V");
        readings.Add(GridFrequency, Num(reading, "freq"), "Hz");

        if (bestIsGrid)
        {
            readings.Add(ForPhase(GridCurrent, 1), bestPhases == 1 ? Num(reading, "current") : null, "A");
            // "Delivered" by the grid is what the house imported.
            readings.Add(GridImportTotalMetric, Num(reading, "actEnergyDlvd") / 1000, "kWh");
            readings.Add(GridExportTotalMetric, Num(reading, "actEnergyRcvd") / 1000, "kWh");
        }
    }

    // ---- /ivp/ensemble/secctrl, /ivp/ensemble/power (IQ Battery) ----

    public static void DecodeBatteries(JsonElement? secctrl, JsonElement? power, Readings readings)
    {
        if (secctrl is { } s) readings.Add(BatterySoc, Num(s, "ENC_agg_soc") ?? Num(s, "agg_soc"), "%");
        if (power is { } p && p.ValueKind == JsonValueKind.Object &&
            (p.TryGetProperty("devices:", out var devices) || p.TryGetProperty("devices", out devices)) &&
            devices.ValueKind == JsonValueKind.Array && devices.GetArrayLength() > 0)
        {
            var milliwatts = devices.EnumerateArray().Sum(d => Num(d, "real_power_mw") ?? 0);
            readings.Add(BatteryPower, -milliwatts / 1000, "W"); // + charging (Enphase: + discharging; unverified)
        }
    }

    public static void AddTokenDaysLeft(DateTimeOffset? expiry, Readings readings)
    {
        if (expiry is { } e) readings.Add(TokenDaysLeftMetric, Math.Floor((e - readings.Timestamp).TotalDays), "days");
    }

    // ---- JSON helpers ----

    private static IEnumerable<JsonElement> Array(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray() : [];

    private static IEnumerable<JsonElement> Items(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

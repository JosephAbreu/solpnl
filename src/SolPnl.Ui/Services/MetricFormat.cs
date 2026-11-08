using System.Globalization;
using SolPnl.Contracts.Models;

namespace SolPnl.Ui.Services;

/// <summary>Display names and number formatting for metrics.</summary>
public static class MetricFormat
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [Metrics.PvPower] = "Solar power",
        [Metrics.PvVoltage] = "PV voltage",
        [Metrics.PvCurrent] = "PV current",
        [Metrics.BatterySoc] = "Battery charge",
        [Metrics.BatteryVoltage] = "Battery voltage",
        [Metrics.BatteryCurrent] = "Battery current",
        [Metrics.BatteryPower] = "Battery power",
        [Metrics.LoadPower] = "Load",
        [Metrics.LoadApparentPower] = "Load (apparent)",
        [Metrics.LoadCurrent] = "Load current",
        [Metrics.LoadPercent] = "Load (% of rated)",
        [Metrics.GridPower] = "Grid power",
        [Metrics.GridVoltage] = "Grid voltage",
        [Metrics.GridCurrent] = "Grid current",
        [Metrics.GridApparentPower] = "Grid (apparent)",
        [Metrics.GridFrequency] = "Grid frequency",
        [Metrics.InverterVoltage] = "Inverter output voltage",
        [Metrics.InverterCurrent] = "Inverter output current",
        [Metrics.InverterFrequency] = "Inverter output frequency",
        [Metrics.InverterTemperature] = "Inverter temperature",
        [Metrics.FaultCode] = "Fault code",
        ["vendor.srne.state"] = "Operating state (SRNE)",
        ["vendor.srne.dc_bus_voltage"] = "DC bus voltage (SRNE)",
        [WeatherMetrics.Temperature] = "Outdoor temperature",
        [WeatherMetrics.Humidity] = "Humidity",
        [WeatherMetrics.CloudCover] = "Cloud cover",
        [WeatherMetrics.WindSpeed] = "Wind speed",
        [WeatherMetrics.Precipitation] = "Precipitation",
        [WeatherMetrics.Irradiance] = "Sunlight (irradiance)",
        [WeatherMetrics.Pressure] = "Barometric pressure",
    };

    /// <summary>Display unit for temperatures: "F" or "C". Values are stored in °C and converted for display.</summary>
    public static string TemperatureUnit { get; set; } = TemperatureUnits.Fahrenheit;

    /// <summary>
    /// Converts a stored value to the user's display units. The "F" choice means US units throughout:
    /// °C -> °F and hPa -> inHg (wind is converted where it's shown).
    /// </summary>
    public static (double Value, string? Unit) ToDisplay(double value, string? unit) =>
        TemperatureUnit != TemperatureUnits.Fahrenheit ? (value, unit) : unit switch
        {
            "°C" => (value * 9 / 5 + 32, "°F"),
            "hPa" => (value * HpaToInHg, "inHg"),
            _ => (value, unit),
        };

    public const double HpaToInHg = 0.0295299830714;
    public const double MetersPerFoot = 0.3048;

    /// <summary>
    /// Money with the user's currency symbol in front, e.g. "$1,234.56"; whole amounts of 1,000 or more drop the cents.
    /// </summary>
    public static string Money(double? amount, string currency) => amount switch
    {
        null => "–",
        { } a when Math.Abs(a) >= 1000 => currency + a.ToString("N0", CultureInfo.CurrentCulture),
        { } a => currency + a.ToString("N2", CultureInfo.CurrentCulture),
    };

    /// <summary>Unit for elevations: "ft" with US units, otherwise "m". Elevations are stored in meters.</summary>
    public static string ElevationUnit => TemperatureUnit == TemperatureUnits.Fahrenheit ? "ft" : "m";

    /// <summary>Meters as an editable number in <see cref="ElevationUnit"/>: whole feet, or meters to 0.1.</summary>
    public static string ElevationInput(double meters) => ElevationUnit == "ft"
        ? (meters / MetersPerFoot).ToString("0", CultureInfo.InvariantCulture)
        : meters.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A number entered in <see cref="ElevationUnit"/>, back to meters.</summary>
    public static double ElevationToMeters(double value) => ElevationUnit == "ft" ? value * MetersPerFoot : value;

    /// <summary>Pressure in hPa formatted in the display unit, e.g. "30.09 inHg" or "1,019 hPa".</summary>
    public static string Pressure(double? hpa) => hpa is { } p ? Format(p, "hPa") : "–";

    /// <summary>Temperature in °C formatted in the display unit, e.g. "72 °F".</summary>
    public static string Temperature(double? celsius) => celsius is { } c ? Format(c, "°C") : "–";

    /// <summary>
    /// "pv.power.1" => "Solar power · string 1", "load.power.l2" => "Load · L2". <paramref name="pvInput"/> is what the
    /// device's vendor calls a PV input (<see cref="PluginInfo.PvInputName"/>, e.g. "line").
    /// </summary>
    public static string DisplayName(string metric, string pvInput = "string")
    {
        if (Names.TryGetValue(metric, out var name)) return name;

        var lastDot = metric.LastIndexOf('.');
        if (lastDot > 0 && Names.TryGetValue(metric[..lastDot], out var baseName))
        {
            var suffix = metric[(lastDot + 1)..];
            if (suffix.Length > 1 && suffix[0] == 'l' && char.IsDigit(suffix[1])) return $"{baseName} · L{suffix[1..]}";
            if (int.TryParse(suffix, out var n))
                return baseName switch
                {
                    "Inverter temperature" => $"{baseName} · sensor {n}",
                    "Fault code" => $"{baseName} · slot {n}",
                    _ => $"{baseName} · {pvInput} {n}",
                };
        }
        return metric;
    }

    /// <summary>Value with its unit (converted to the display unit), at a precision suited to the unit.</summary>
    public static string Format(double value, string? unit)
    {
        (value, unit) = ToDisplay(value, unit);
        var number = unit switch
        {
            "Hz" or "inHg" => value.ToString("N2", CultureInfo.CurrentCulture),
            "hPa" => value.ToString("N0", CultureInfo.CurrentCulture),
            "W" or "VA" or "%" or "" or null or "W/m²" or "°F" => value.ToString("N0", CultureInfo.CurrentCulture),
            _ => value.ToString(Math.Abs(value) >= 1000 ? "N0" : "N1", CultureInfo.CurrentCulture),
        };
        return string.IsNullOrEmpty(unit) ? number : unit == "%" ? $"{number}%" : $"{number} {unit}";
    }

    /// <summary>
    /// Axis tick label: in full under 10,000, compact above (10K, 20K), with exactly as many decimals as the
    /// tick step needs, so neighbouring ticks never print the same label (e.g. 122.2 / 122.4 V, not 122 / 122).
    /// </summary>
    public static string Tick(double value, double step)
    {
        var culture = CultureInfo.CurrentCulture;
        if (Math.Abs(value) >= 10_000 && step >= 1000)
            return (value / 1000).ToString("N" + DecimalsFor(step / 1000), culture) + "K";
        return value.ToString("N" + DecimalsFor(step), culture);
    }

    /// <summary>Fewest decimals that represent multiples of <paramref name="step"/> exactly (0.25 => 2, 0.2 => 1, 5 => 0).</summary>
    public static int DecimalsFor(double step)
    {
        step = Math.Abs(step);
        for (var d = 0; d < 6; d++)
        {
            var scaled = step * Math.Pow(10, d);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-6) return d;
        }
        return 6;
    }

    /// <summary>"Line 1" for <paramref name="pvInput"/> "line".</summary>
    public static string PvInput(string pvInput, int number) =>
        $"{char.ToUpperInvariant(pvInput[0])}{pvInput[1..]} {number}";

    /// <summary>"Mains powered (4)", or "State 12" when the plugin doesn't know the value.</summary>
    public static string State(DeviceOperatingState state) =>
        state.Description is null ? $"State {state.Value}" : $"{state.Description} ({state.Value})";

    public static string Ago(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var age = now - timestamp;
        if (age < TimeSpan.FromSeconds(5)) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>Plain-language description of a WMO weather code (as used by Open-Meteo).</summary>
    public static string WeatherText(int? code) => code switch
    {
        0 => "Clear",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        null => "–",
        _ => "Unknown",
    };

    public static string Duration(TimeSpan span) => $"{(int)span.TotalHours}h {span.Minutes:00}m";

    public static string Time(DateTimeOffset? t) =>
        t is { } v ? v.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture) : "–";
}

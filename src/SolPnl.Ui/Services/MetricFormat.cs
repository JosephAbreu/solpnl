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
        [Metrics.GridPower] = "Grid power",
        [Metrics.GridVoltage] = "Grid voltage",
        [Metrics.GridFrequency] = "Grid frequency",
        [Metrics.InverterVoltage] = "Inverter output voltage",
        [Metrics.InverterCurrent] = "Inverter output current",
        [Metrics.InverterFrequency] = "Inverter output frequency",
        [Metrics.InverterTemperature] = "Inverter temperature",
        ["vendor.srne.state"] = "Operating state (SRNE)",
    };

    /// <summary>"pv.power.1" => "Solar power · string 1", "load.power.l2" => "Load · L2".</summary>
    public static string DisplayName(string metric)
    {
        if (Names.TryGetValue(metric, out var name)) return name;

        var lastDot = metric.LastIndexOf('.');
        if (lastDot > 0 && Names.TryGetValue(metric[..lastDot], out var baseName))
        {
            var suffix = metric[(lastDot + 1)..];
            if (suffix.Length > 1 && suffix[0] == 'l' && char.IsDigit(suffix[1])) return $"{baseName} · L{suffix[1..]}";
            if (int.TryParse(suffix, out var n))
                return baseName.StartsWith("Inverter temperature", StringComparison.Ordinal) ? $"{baseName} · sensor {n}" : $"{baseName} · string {n}";
        }
        return metric;
    }

    /// <summary>Value with its unit, at a precision suited to the unit.</summary>
    public static string Format(double value, string? unit)
    {
        var number = unit switch
        {
            "Hz" => value.ToString("N2", CultureInfo.CurrentCulture),
            "W" or "VA" or "%" or "" or null => value.ToString("N0", CultureInfo.CurrentCulture),
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

    public static string Ago(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var age = now - timestamp;
        if (age < TimeSpan.FromSeconds(5)) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }
}

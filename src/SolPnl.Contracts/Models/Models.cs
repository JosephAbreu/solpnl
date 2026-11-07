namespace SolPnl.Contracts.Models;

public sealed record DeviceInfo(
    string DeviceId,
    string Vendor,
    string Model,
    string? SerialNumber,
    string? FirmwareVersion);

/// <summary>A single measured value from a device at a point in time.</summary>
/// <param name="Metric">Prefer a constant from <see cref="Metrics"/>; vendor-specific values may use "vendor.*" names.</param>
public sealed record Reading(
    string DeviceId,
    string Metric,
    double Value,
    string Unit,
    DateTimeOffset Timestamp);

public enum HistoryResolution
{
    /// <summary>Every stored poll.</summary>
    Raw,
    /// <summary>One point per minute (mean, with min/max).</summary>
    Minute,
    /// <summary>One point per hour (mean, with min/max).</summary>
    Hour,
}

/// <summary>
/// One point in a metric's history. For <see cref="HistoryResolution.Raw"/>, <see cref="Value"/> is the reading and
/// Min/Max are null. For summaries, <see cref="Timestamp"/> is the bucket start, <see cref="Value"/> is the mean,
/// and Min/Max are the extremes within the bucket.
/// </summary>
public sealed record HistoryPoint(DateTimeOffset Timestamp, double Value, double? Min = null, double? Max = null);

public sealed record HistoryResult(
    string DeviceId,
    string Metric,
    string? Unit,
    HistoryResolution Resolution,
    IReadOnlyList<HistoryPoint> Points);

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

public sealed record Alert(
    string Id,
    AlertSeverity Severity,
    string Title,
    string Message,
    DateTimeOffset RaisedAt,
    string? DeviceId = null);

/// <summary>
/// Vendor-neutral metric names so the UI and alert rules work across plugins.
/// Totals use the bare name; per-PV-string values use <see cref="ForString"/> ("pv.power.1") and
/// per-phase values use <see cref="ForPhase"/> ("load.power.l1"). Vendor-specific values use "vendor.&lt;plugin&gt;.*".
/// </summary>
public static class Metrics
{
    public const string PvPower = "pv.power";             // W
    public const string PvVoltage = "pv.voltage";         // V
    public const string PvCurrent = "pv.current";         // A
    public const string BatterySoc = "battery.soc";       // %
    public const string BatteryVoltage = "battery.voltage"; // V
    public const string BatteryCurrent = "battery.current"; // A, + charging / - discharging
    public const string BatteryPower = "battery.power";   // W, + charging / - discharging
    public const string LoadPower = "load.power";         // W
    public const string LoadApparentPower = "load.apparent_power"; // VA
    public const string LoadCurrent = "load.current";     // A
    public const string LoadPercent = "load.percent";     // %, of the inverter's rated output
    public const string GridPower = "grid.power";         // W, + import / - export
    public const string GridVoltage = "grid.voltage";     // V
    public const string GridCurrent = "grid.current";     // A
    public const string GridApparentPower = "grid.apparent_power"; // VA
    public const string GridFrequency = "grid.frequency"; // Hz
    public const string InverterVoltage = "inverter.voltage";     // V, AC output
    public const string InverterCurrent = "inverter.current";     // A, AC output
    public const string InverterFrequency = "inverter.frequency"; // Hz, AC output
    public const string InverterTemperature = "inverter.temperature"; // °C

    /// <summary>Per-PV-string metric, e.g. ForString(PvPower, 1) => "pv.power.1".</summary>
    public static string ForString(string metric, int stringNumber) => $"{metric}.{stringNumber}";

    /// <summary>Per-phase metric, e.g. ForPhase(LoadPower, 2) => "load.power.l2".</summary>
    public static string ForPhase(string metric, int phase) => $"{metric}.l{phase}";
}

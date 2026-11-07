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

/// <summary>Vendor-neutral metric names so the UI and alert rules work across plugins.</summary>
public static class Metrics
{
    public const string PvPower = "pv.power";             // W, total
    public const string PvVoltage = "pv.voltage";         // V, per string: append ".1", ".2"
    public const string BatterySoc = "battery.soc";       // %
    public const string BatteryVoltage = "battery.voltage"; // V
    public const string BatteryCurrent = "battery.current"; // A, + charging / - discharging
    public const string BatteryPower = "battery.power";   // W, + charging / - discharging
    public const string LoadPower = "load.power";         // W
    public const string GridPower = "grid.power";         // W, + import / - export
    public const string GridVoltage = "grid.voltage";     // V
    public const string InverterTemperature = "inverter.temperature"; // °C
}

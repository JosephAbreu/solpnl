namespace SolPnl.Core.Storage;

/// <summary>
/// How long each resolution is kept ("Retention" config section). 0 keeps data forever.
/// Data is only deleted after it has been summarised into the next resolution.
/// </summary>
public sealed class RetentionOptions
{
    public int RawDays { get; set; } = 7;
    public int MinuteDays { get; set; } = 90;
    public int HourDays { get; set; } = 0;

    /// <summary>Seconds between rollup and retention runs.</summary>
    public int MaintenanceIntervalSeconds { get; set; } = 60;
}

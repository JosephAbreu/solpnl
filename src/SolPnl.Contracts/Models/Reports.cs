namespace SolPnl.Contracts.Models;

public static class ReportScopes
{
    /// <summary>One period per year.</summary>
    public const string All = "all";
    /// <summary>One period per month of a year.</summary>
    public const string Year = "year";
    /// <summary>One period per day of a month.</summary>
    public const string Month = "month";
}

/// <summary>
/// Energy totals for a day, month or year. Values are null when no day in the period recorded them.
/// <paramref name="SelfPoweredPercent"/> is solar ÷ load: the share of the home's use that solar covered.
/// </summary>
public sealed record EnergyPeriod(
    DateOnly Start,
    int Days,
    double? SolarKwh,
    double? LoadKwh,
    double? GridKwh,
    double? Saved,
    double? SelfPoweredPercent);

/// <summary>
/// A report for <paramref name="Scope"/> (<see cref="ReportScopes"/>). <paramref name="Previous"/> is the same
/// period a year earlier (for a period still in progress, only up to the same day), or null if there's no data for it.
/// <paramref name="PreviousPeriods"/> lines up with <paramref name="Periods"/> for the year view (each month a year
/// earlier); it's empty for the other views.
/// </summary>
public sealed record EnergyReport(
    string Scope,
    int? Year,
    int? Month,
    string? Currency,
    EnergyPeriod Total,
    EnergyPeriod? Previous,
    IReadOnlyList<EnergyPeriod> Periods,
    IReadOnlyList<EnergyPeriod?> PreviousPeriods,
    DaySummary? BestDay,
    IReadOnlyList<int> Years,
    DateOnly Today);

using System.Text.Json.Serialization;

namespace SolPnl.Contracts.Models;

/// <summary>
/// The price of grid electricity from <see cref="From"/> on (until the next rate's From). <see cref="SummerPricePerKwh"/>
/// applies June–September if set (many US utilities charge more in summer). <see cref="From"/> null = since the start.
/// </summary>
public sealed record ElectricityRate(DateOnly? From, double PricePerKwh, double? SummerPricePerKwh = null);

/// <summary>
/// What solar saves: every kWh of solar used at home is a kWh not bought from the grid at the rate in effect that day.
/// <see cref="SystemCost"/> (optional) gives the payback progress.
/// </summary>
public sealed record SavingsSettings(string Currency = "$", IReadOnlyList<ElectricityRate>? Rates = null, double? SystemCost = null)
{
    [JsonIgnore]
    public IReadOnlyList<ElectricityRate> RateList => Rates ?? [];
}

/// <summary>Savings for one calendar month.</summary>
public sealed record MonthSavings(int Year, int Month, double SolarKwh, double Saved);

/// <summary>
/// Money saved by solar, in <see cref="Currency"/>. Null amounts until a rate is set (<see cref="Configured"/>).
/// <see cref="Since"/> is the first day with solar data.
/// </summary>
public sealed record SavingsSummary(
    bool Configured,
    string Currency,
    double? Today,
    double? ThisMonth,
    double? ThisYear,
    double? Lifetime,
    double LifetimeSolarKwh,
    DateOnly? Since,
    double? SystemCost,
    double? PaybackPercent,
    double? CurrentPricePerKwh,
    IReadOnlyList<MonthSavings> Months);

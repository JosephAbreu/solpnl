namespace SolPnl.Contracts.Models;

/// <summary>Where SolPnl's equipment is. Used for sun times, weather and solar insights.</summary>
/// <param name="TimeZoneId">IANA time zone, e.g. "America/Chicago". Days (sunrise, daily highs/lows, energy) use it.</param>
public sealed record SiteLocation(
    string? Name,
    double Latitude,
    double Longitude,
    double? ElevationMeters,
    string TimeZoneId);

/// <summary>Sun events for one local day. Null rise/set means the sun doesn't rise or set (polar day/night).</summary>
public sealed record SunTimes(
    DateOnly Date,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset,
    DateTimeOffset SolarNoon,
    TimeSpan DayLength,
    DateTimeOffset? CivilDawn,
    DateTimeOffset? CivilDusk);

/// <summary>Current conditions. Temperatures in °C, wind in km/h, precipitation in mm, irradiance in W/m².</summary>
public sealed record WeatherNow(
    DateTimeOffset Time,
    double? TemperatureC,
    double? HumidityPercent,
    double? CloudCoverPercent,
    double? WindSpeedKmh,
    double? PrecipitationMm,
    double? IrradianceWm2,
    int? WeatherCode,
    bool? IsDay);

/// <summary>One forecast hour. <see cref="IrradianceWm2"/> is global horizontal irradiance (mean over the hour).</summary>
public sealed record HourlyForecast(
    DateTimeOffset Time,
    double? TemperatureC,
    double? CloudCoverPercent,
    double? IrradianceWm2,
    int? PrecipitationProbability);

/// <summary>One forecast day (local date). <see cref="IrradiationKwhM2"/> is the day's total sunlight on a horizontal surface.</summary>
public sealed record DailyForecast(
    DateOnly Date,
    double? HighC,
    double? LowC,
    double? IrradiationKwhM2,
    int? PrecipitationProbability,
    int? WeatherCode);

public sealed record WeatherReport(
    string Source,
    WeatherNow? Current,
    IReadOnlyList<HourlyForecast> Hourly,
    IReadOnlyList<DailyForecast> Daily,
    DateTimeOffset RetrievedAt);

/// <summary>An official weather alert (watch, warning, advisory) affecting the location.</summary>
public sealed record WeatherAlert(
    string Id,
    string Event,
    string? Headline,
    string Severity,
    string? Urgency,
    DateTimeOffset? Effective,
    DateTimeOffset? Expires,
    string? Description,
    string? Instruction,
    string? Source);

public sealed record LocationSearchResult(
    string Name,
    string? Region,
    string? Country,
    double Latitude,
    double Longitude,
    double? ElevationMeters,
    string? TimeZoneId);

/// <summary>
/// One local day: sun times, the forecast as it stood before the day began, what actually happened (temperatures
/// from logged weather, energy from logged device readings).
/// </summary>
public sealed record DaySummary(
    DateOnly Date,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset,
    double? ForecastHighC,
    double? ForecastLowC,
    double? ForecastIrradiationKwhM2,
    double? ClearSkyIrradiationKwhM2,
    double? ActualHighC,
    double? ActualLowC,
    double? SolarKwh,
    double? LoadKwh,
    double? GridKwh);

public static class InsightKinds
{
    public const string SolarOutlook = "solar-outlook";
    public const string Underperformance = "underperformance";
    public const string BestTime = "best-time";
}

public static class InsightLevels
{
    public const string Info = "info";
    public const string Good = "good";
    public const string Warning = "warning";
}

/// <summary>A recommendation or observation SolPnl derived from weather, sun and device data.</summary>
public sealed record Insight(string Kind, string Level, string Title, string Message);

/// <summary>Everything the dashboard's "today" section needs in one call.</summary>
public sealed record SiteOverview(
    SiteLocation? Location,
    SunTimes? Today,
    SunTimes? Tomorrow,
    WeatherNow? Weather,
    string? WeatherSource,
    DateTimeOffset? WeatherUpdated,
    DaySummary? TodaySummary,
    DailyForecast? TomorrowForecast,
    IReadOnlyList<Insight> Insights,
    IReadOnlyList<WeatherAlert> Alerts);

public sealed record SiteSettings(SiteLocation? Location, string TemperatureUnit);

public sealed record SetUnitsRequest(string TemperatureUnit);

public static class TemperatureUnits
{
    public const string Celsius = "C";
    public const string Fahrenheit = "F";
}

/// <summary>Reserved device key under which weather conditions are logged as readings (so they get history and charts).</summary>
public static class SiteDevices
{
    public const string Weather = "site-weather";
}

/// <summary>Weather metric names, logged under <see cref="SiteDevices.Weather"/>.</summary>
public static class WeatherMetrics
{
    public const string Temperature = "weather.temperature";   // °C
    public const string Humidity = "weather.humidity";         // %
    public const string CloudCover = "weather.cloud_cover";    // %
    public const string WindSpeed = "weather.wind_speed";      // km/h
    public const string Precipitation = "weather.precipitation"; // mm
    public const string Irradiance = "weather.irradiance";     // W/m², global horizontal
}

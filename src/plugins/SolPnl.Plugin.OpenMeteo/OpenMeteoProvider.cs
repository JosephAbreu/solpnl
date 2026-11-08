using System.Globalization;
using System.Text.Json;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.OpenMeteo;

/// <summary>
/// Weather from Open-Meteo (open-meteo.com): free, no API key, built on national forecast models (NOAA GFS/HRRR,
/// ECMWF, DWD ICON...). Also forecasts solar irradiance, which SolPnl uses to predict solar output, and offers place
/// search for the location settings. Free for non-commercial use.
/// </summary>
public sealed class OpenMeteoProvider(HttpClient http) : IWeatherProvider, ILocationSearch
{
    public const string PluginId = "open-meteo";

    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";

    public string Id => PluginId;
    public string DisplayName => "Open-Meteo weather";

    public string Description =>
        "Current conditions, forecasts and solar irradiance from open-meteo.com (national weather models, no API key). " +
        "Also finds coordinates for the location settings.";

    public async Task<WeatherReport> GetWeatherAsync(SiteLocation location, CancellationToken cancellationToken = default)
    {
        var url = ForecastUrl +
                  $"?latitude={F(location.Latitude)}&longitude={F(location.Longitude)}" +
                  (location.ElevationMeters is { } e ? $"&elevation={F(e)}" : "") +
                  "&current=temperature_2m,relative_humidity_2m,cloud_cover,wind_speed_10m,precipitation,weather_code,shortwave_radiation,is_day" +
                  "&hourly=temperature_2m,cloud_cover,shortwave_radiation,precipitation_probability" +
                  "&daily=temperature_2m_max,temperature_2m_min,shortwave_radiation_sum,precipitation_probability_max,weather_code" +
                  $"&timezone={Uri.EscapeDataString(location.TimeZoneId)}&timeformat=unixtime&forecast_days=3";

        using var response = await http.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseForecast(json.RootElement, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<LocationSearchResult>> SearchLocationsAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{GeocodingUrl}?name={Uri.EscapeDataString(query.Trim())}&count=8&language=en&format=json";
        using var response = await http.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseGeocoding(json.RootElement);
    }

    /// <summary>Parses a forecast response requested with timeformat=unixtime.</summary>
    public static WeatherReport ParseForecast(JsonElement root, DateTimeOffset retrievedAt)
    {
        var zone = ZoneOf(root);

        WeatherNow? current = null;
        if (root.TryGetProperty("current", out var c))
        {
            current = new WeatherNow(
                Time: DateTimeOffset.FromUnixTimeSeconds(c.GetProperty("time").GetInt64()),
                TemperatureC: Num(c, "temperature_2m"),
                HumidityPercent: Num(c, "relative_humidity_2m"),
                CloudCoverPercent: Num(c, "cloud_cover"),
                WindSpeedKmh: Num(c, "wind_speed_10m"),
                PrecipitationMm: Num(c, "precipitation"),
                IrradianceWm2: Num(c, "shortwave_radiation"),
                WeatherCode: (int?)Num(c, "weather_code"),
                IsDay: Num(c, "is_day") is { } d ? d > 0 : null);
        }

        var hourly = new List<HourlyForecast>();
        if (root.TryGetProperty("hourly", out var h) && h.TryGetProperty("time", out var hTimes))
        {
            for (var i = 0; i < hTimes.GetArrayLength(); i++)
            {
                hourly.Add(new HourlyForecast(
                    DateTimeOffset.FromUnixTimeSeconds(hTimes[i].GetInt64()),
                    At(h, "temperature_2m", i),
                    At(h, "cloud_cover", i),
                    At(h, "shortwave_radiation", i),
                    (int?)At(h, "precipitation_probability", i)));
            }
        }

        var daily = new List<DailyForecast>();
        if (root.TryGetProperty("daily", out var d2) && d2.TryGetProperty("time", out var dTimes))
        {
            for (var i = 0; i < dTimes.GetArrayLength(); i++)
            {
                var localMidnight = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(dTimes[i].GetInt64()), zone);
                daily.Add(new DailyForecast(
                    DateOnly.FromDateTime(localMidnight.DateTime),
                    At(d2, "temperature_2m_max", i),
                    At(d2, "temperature_2m_min", i),
                    At(d2, "shortwave_radiation_sum", i) is { } mj ? Math.Round(mj / 3.6, 3) : null, // MJ/m² -> kWh/m²
                    (int?)At(d2, "precipitation_probability_max", i),
                    (int?)At(d2, "weather_code", i)));
            }
        }

        return new WeatherReport("Open-Meteo", current, hourly, daily, retrievedAt);
    }

    public static IReadOnlyList<LocationSearchResult> ParseGeocoding(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Select(r => new LocationSearchResult(
            Str(r, "name") ?? "Unknown",
            Str(r, "admin1"),
            Str(r, "country"),
            r.GetProperty("latitude").GetDouble(),
            r.GetProperty("longitude").GetDouble(),
            Num(r, "elevation"),
            Str(r, "timezone"))).ToList();
    }

    private static TimeZoneInfo ZoneOf(JsonElement root)
    {
        if (Str(root, "timezone") is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        var offset = TimeSpan.FromSeconds(Num(root, "utc_offset_seconds") ?? 0);
        return TimeZoneInfo.CreateCustomTimeZone("open-meteo", offset, "open-meteo", "open-meteo");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        // Open-Meteo explains errors as { "error": true, "reason": "..." }.
        var body = await response.Content.ReadAsStringAsync(ct);
        string? reason = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            reason = Str(json.RootElement, "reason");
        }
        catch (JsonException)
        {
        }
        throw new HttpRequestException($"Open-Meteo answered {(int)response.StatusCode}{(reason is null ? "" : $": {reason}")}");
    }

    private static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double? At(JsonElement parent, string name, int index) =>
        parent.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && index < arr.GetArrayLength()
            && arr[index].ValueKind == JsonValueKind.Number
            ? arr[index].GetDouble()
            : null;
}

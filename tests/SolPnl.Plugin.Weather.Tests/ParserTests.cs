using System.Text.Json;
using SolPnl.Plugin.NwsAlerts;
using SolPnl.Plugin.OpenMeteo;

namespace SolPnl.Plugin.Weather.Tests;

/// <summary>Parsers run against real API responses captured on 2026-11-07 (Austin, TX).</summary>
public class ParserTests
{
    private static JsonElement Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))).RootElement;

    [Fact]
    public void OpenMeteo_Forecast_ParsesCurrentHourlyAndDaily()
    {
        var retrieved = DateTimeOffset.UnixEpoch;
        var report = OpenMeteoProvider.ParseForecast(Fixture("open-meteo-forecast.json"), retrieved);

        Assert.Equal("Open-Meteo", report.Source);
        Assert.Equal(retrieved, report.RetrievedAt);

        var now = Assert.IsType<SolPnl.Contracts.Models.WeatherNow>(report.Current);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794095100), now.Time);
        Assert.Equal(20.3, now.TemperatureC);
        Assert.Equal(68, now.HumidityPercent);
        Assert.Equal(100, now.CloudCoverPercent);
        Assert.Equal(3, now.WeatherCode);
        Assert.Equal(0, now.IrradianceWm2);
        Assert.False(now.IsDay);

        Assert.Equal(72, report.Hourly.Count);
        Assert.All(report.Hourly, h => Assert.Equal(TimeSpan.Zero, h.Time.Offset));

        // Daily dates are local (America/Chicago) dates; MJ/m² converted to kWh/m².
        Assert.Equal([new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9)], report.Daily.Select(d => d.Date));
        Assert.Equal(21.4, report.Daily[0].HighC);
        Assert.Equal(13.6, report.Daily[0].LowC);
        Assert.Equal(Math.Round(9.68 / 3.6, 3), report.Daily[0].IrradiationKwhM2);
        Assert.Equal(69, report.Daily[1].PrecipitationProbability);
    }

    [Fact]
    public void OpenMeteo_Geocoding_ParsesPlaces()
    {
        var results = OpenMeteoProvider.ParseGeocoding(Fixture("open-meteo-geocoding.json"));
        var austin = results[0];
        Assert.Equal("Austin", austin.Name);
        Assert.Equal("Texas", austin.Region);
        Assert.Equal("United States", austin.Country);
        Assert.Equal(30.26715, austin.Latitude, 4);
        Assert.Equal(-97.74306, austin.Longitude, 4);
        Assert.Equal("America/Chicago", austin.TimeZoneId);
        Assert.True(results.Count > 1);
    }

    [Fact]
    public void OpenMeteo_Geocoding_NoResults() =>
        Assert.Empty(OpenMeteoProvider.ParseGeocoding(JsonDocument.Parse("{\"generationtime_ms\":0.1}").RootElement));

    [Fact]
    public void Nws_ParsesAlerts()
    {
        var alerts = NwsAlertProvider.Parse(Fixture("nws-alerts.json"));
        Assert.Equal(2, alerts.Count);
        var first = alerts[0];
        Assert.Equal("Storm Warning", first.Event);
        Assert.Equal("Severe", first.Severity);
        Assert.NotNull(first.Expires);
        Assert.False(string.IsNullOrWhiteSpace(first.Headline));
        Assert.StartsWith("urn:oid:", first.Id);
    }

    [Fact]
    public void Nws_OrdersMostSevereFirst_AndSkipsIncomplete()
    {
        var json = JsonDocument.Parse("""
            { "features": [
              { "properties": { "id": "a", "event": "Wind Advisory", "severity": "Moderate" } },
              { "properties": { "id": "b", "event": "Tornado Warning", "severity": "Extreme" } },
              { "properties": { "event": "No id" } },
              { "properties": { "id": "c", "event": "Frost Advisory", "severity": "Minor" } }
            ] }
            """).RootElement;
        Assert.Equal(["Tornado Warning", "Wind Advisory", "Frost Advisory"], NwsAlertProvider.Parse(json).Select(a => a.Event));
    }
}

public class PressureParsingTests
{
    [Fact]
    public void OpenMeteo_ParsesSeaLevelPressure()
    {
        var json = JsonDocument.Parse("""
            { "timezone": "America/Chicago", "utc_offset_seconds": -21600,
              "current": { "time": 1794095100, "temperature_2m": 20.3, "pressure_msl": 1019.1, "surface_pressure": 999.8 } }
            """).RootElement;
        var report = OpenMeteoProvider.ParseForecast(json, DateTimeOffset.UnixEpoch);
        Assert.Equal(1019.1, report.Current!.PressureHpa);
    }

    [Fact]
    public void OpenMeteo_PressureMissing_IsNull()
    {
        var json = JsonDocument.Parse("""{ "current": { "time": 1794095100, "temperature_2m": 20.3 } }""").RootElement;
        Assert.Null(OpenMeteoProvider.ParseForecast(json, DateTimeOffset.UnixEpoch).Current!.PressureHpa);
    }
}

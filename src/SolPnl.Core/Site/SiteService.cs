using Microsoft.Extensions.Logging;
using SolPnl.Contracts.Models;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Site;

/// <summary>
/// Location-aware data: weather (logged as readings under <see cref="SiteDevices.Weather"/> so it gets history and
/// charts), weather alerts, per-day summaries (sun times, forecast vs actual temperatures, solar/load/grid energy)
/// and the insights built from them.
/// </summary>
public sealed class SiteService(
    SiteSettingsStore settings,
    PluginCatalog plugins,
    DaySummaryStore days,
    IReadingStore store,
    TimeProvider time,
    ILogger<SiteService> logger)
{
    private volatile WeatherReport? _weather;
    private volatile IReadOnlyList<WeatherAlert> _alerts = [];
    private string? _weatherForLocation;

    public WeatherReport? LastWeather => _weather;
    public IReadOnlyList<WeatherAlert> Alerts => _alerts;

    /// <summary>Fetches weather from the enabled provider, logs current conditions and records the forecast.</summary>
    public async Task RefreshWeatherAsync(CancellationToken ct = default)
    {
        var location = await settings.GetLocationAsync(ct);
        var provider = await plugins.WeatherProviderAsync(ct);
        if (location is null || provider is null)
        {
            _weather = null;
            return;
        }

        var report = await provider.GetWeatherAsync(location, ct);
        _weather = report;
        _weatherForLocation = LocationKey(location);

        if (report.Current is { } now) await LogConditionsAsync(provider.DisplayName, now, ct);

        // Keep each day's forecast as it stood before the day began (fill it in if it's missing), so a day's
        // forecast can be compared with what actually happened.
        var zone = SiteSettingsStore.Zone(location);
        foreach (var day in report.Daily)
        {
            var dayStart = SunCalculator.LocalMidnightUtc(day.Date, zone);
            var beforeDay = time.GetUtcNow() < dayStart;
            await days.UpdateAsync(day.Date, row =>
            {
                if (!beforeDay && row.ForecastHigh is not null) return;
                row.ForecastHigh = day.HighC;
                row.ForecastLow = day.LowC;
                row.ForecastIrradiation = day.IrradiationKwhM2;
            }, ct);
        }
    }

    public async Task RefreshAlertsAsync(CancellationToken ct = default)
    {
        var location = await settings.GetLocationAsync(ct);
        if (location is null)
        {
            _alerts = [];
            return;
        }

        var alerts = new List<WeatherAlert>();
        foreach (var provider in await plugins.AlertProvidersAsync(ct))
        {
            try
            {
                alerts.AddRange(await provider.GetAlertsAsync(location, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Weather alerts from {Provider} failed: {Message}", provider.DisplayName, ex.Message);
            }
        }
        _alerts = alerts;
    }

    /// <summary>Recomputes yesterday's and today's sun times, actual temperatures and energy totals.</summary>
    public async Task UpdateDaySummariesAsync(CancellationToken ct = default)
    {
        var location = await settings.GetLocationAsync(ct);
        if (location is null) return;
        var zone = SiteSettingsStore.Zone(location);
        var today = Today(zone);

        var deviceIds = (await store.GetDevicesAsync(ct)).Select(d => d.DeviceId).Where(id => id != SiteDevices.Weather).ToList();
        foreach (var date in new[] { today.AddDays(-1), today })
        {
            var from = SunCalculator.LocalMidnightUtc(date, zone);
            var to = SunCalculator.LocalMidnightUtc(date.AddDays(1), zone).AddMilliseconds(-1);
            var sun = SunCalculator.Calculate(date, location.Latitude, location.Longitude, zone);
            var clearSky = SunCalculator.ClearSkyIrradiation(date, location.Latitude, location.Longitude, zone);

            var temperatures = await store.GetHistoryAsync(SiteDevices.Weather, WeatherMetrics.Temperature, from, to, HistoryResolution.Minute, ct);
            double? high = temperatures.Points.Count > 0 ? temperatures.Points.Max(p => p.Max ?? p.Value) : null;
            double? low = temperatures.Points.Count > 0 ? temperatures.Points.Min(p => p.Min ?? p.Value) : null;

            var solar = await EnergyWhAsync(deviceIds, Metrics.PvPower, from, to, ct);
            var load = await EnergyWhAsync(deviceIds, Metrics.LoadPower, from, to, ct);
            var grid = await EnergyWhAsync(deviceIds, Metrics.GridPower, from, to, ct);

            await days.UpdateAsync(date, row =>
            {
                row.SunriseMs = sun.Sunrise?.ToUnixTimeMilliseconds();
                row.SunsetMs = sun.Sunset?.ToUnixTimeMilliseconds();
                row.ClearSkyIrradiation = clearSky;
                row.ActualHigh = high is { } h ? Math.Round(h, 1) : null;
                row.ActualLow = low is { } l ? Math.Round(l, 1) : null;
                row.SolarWh = solar;
                row.LoadWh = load;
                row.GridWh = grid;
            }, ct);
        }
    }

    public async Task<SiteOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var location = await settings.GetLocationAsync(ct);
        if (location is null)
            return new SiteOverview(null, null, null, null, null, null, null, null, [], []);

        var zone = SiteSettingsStore.Zone(location);
        var now = time.GetUtcNow();
        var today = Today(zone);
        var sunToday = SunCalculator.Calculate(today, location.Latitude, location.Longitude, zone);
        var sunTomorrow = SunCalculator.Calculate(today.AddDays(1), location.Latitude, location.Longitude, zone);

        // Ignore weather fetched for a previous location until the next refresh.
        var weather = _weatherForLocation == LocationKey(location) ? _weather : null;
        var tomorrowForecast = weather?.Daily.FirstOrDefault(d => d.Date == today.AddDays(1));

        var history = await days.GetRangeAsync(today.AddDays(-30), today, ct);
        var todaySummary = history.FirstOrDefault(d => d.Date == today);
        var pastDays = history.Where(d => d.Date < today).ToList();

        var insights = InsightCalculator.Compute(new InsightCalculator.Inputs(
            now, zone, sunToday, sunTomorrow, weather?.Hourly ?? [], tomorrowForecast,
            SunCalculator.ClearSkyIrradiation(today.AddDays(1), location.Latitude, location.Longitude, zone),
            pastDays));

        var pressureHistory = await store.GetHistoryAsync(SiteDevices.Weather, WeatherMetrics.Pressure,
            now.AddHours(-4), now, HistoryResolution.Raw, ct);
        var pressure = PressureTendency(pressureHistory.Points);

        return new SiteOverview(location, sunToday, sunTomorrow, weather?.Current, weather?.Source, weather?.RetrievedAt,
            todaySummary, tomorrowForecast, insights, _alerts, pressure);
    }

    private async Task LogConditionsAsync(string source, WeatherNow now, CancellationToken ct)
    {
        // Providers update current conditions every ~15 minutes; log each observation once.
        var latest = await store.GetLatestAsync(SiteDevices.Weather, ct);
        if (latest.Count > 0 && latest.Max(r => r.Timestamp) >= now.Time) return;

        var readings = new List<Reading>();
        void Add(string metric, double? value, string unit)
        {
            if (value is { } v) readings.Add(new Reading(SiteDevices.Weather, metric, v, unit, now.Time));
        }
        Add(WeatherMetrics.Temperature, now.TemperatureC, "°C");
        Add(WeatherMetrics.Humidity, now.HumidityPercent, "%");
        Add(WeatherMetrics.CloudCover, now.CloudCoverPercent, "%");
        Add(WeatherMetrics.WindSpeed, now.WindSpeedKmh, "km/h");
        Add(WeatherMetrics.Precipitation, now.PrecipitationMm, "mm");
        Add(WeatherMetrics.Irradiance, now.IrradianceWm2, "W/m²");
        Add(WeatherMetrics.Pressure, now.PressureHpa, "hPa");

        if (readings.Count > 0)
            await store.SaveAsync(new DeviceInfo(SiteDevices.Weather, source, "Weather", null, null), readings, ct);
    }

    /// <summary>Energy (Wh) of a power metric over [from, to], summed across devices, from per-minute averages.</summary>
    private async Task<double?> EnergyWhAsync(IReadOnlyList<string> deviceIds, string metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        double? total = null;
        foreach (var id in deviceIds)
        {
            var history = await store.GetHistoryAsync(id, metric, from, to, HistoryResolution.Minute, ct);
            if (history.Points.Count == 0) continue;
            total = (total ?? 0) + history.Points.Sum(p => p.Value) / 60.0; // each point is a 1-minute average in W
        }
        return total is { } t ? Math.Round(t, 1) : null;
    }

    /// <summary>Change of at least this much in 3 hours counts as rising/falling (barometric tendency).</summary>
    public const double TendencyThresholdHpa = 1.0;

    /// <summary>Change of at least this much in 3 hours counts as rapid (a strong sign of changing weather).</summary>
    public const double RapidThresholdHpa = 3.0;

    /// <summary>
    /// Latest pressure and its change from about 3 hours earlier (the reading closest to that time, within 45
    /// minutes). Null without readings; no change figure until 3 hours of history exist.
    /// </summary>
    public static PressureTrend? PressureTendency(IReadOnlyList<HistoryPoint> points)
    {
        if (points.Count == 0) return null;
        var latest = points.MaxBy(p => p.Timestamp)!;
        var target = latest.Timestamp.AddHours(-3);
        var earlier = points
            .Where(p => (p.Timestamp - target).Duration() <= TimeSpan.FromMinutes(45))
            .MinBy(p => (p.Timestamp - target).Duration());
        if (earlier is null) return new PressureTrend(latest.Value, null, "steady", false);

        var change = Math.Round(latest.Value - earlier.Value, 1);
        var tendency = change >= TendencyThresholdHpa ? "rising" : change <= -TendencyThresholdHpa ? "falling" : "steady";
        return new PressureTrend(latest.Value, change, tendency, Math.Abs(change) >= RapidThresholdHpa);
    }

    private DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);

    private static string LocationKey(SiteLocation l) => $"{l.Latitude},{l.Longitude},{l.TimeZoneId}";
}

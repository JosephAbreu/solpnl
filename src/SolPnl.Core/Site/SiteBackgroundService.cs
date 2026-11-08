using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SolPnl.Core.Site;

public sealed class SiteOptions
{
    /// <summary>Minutes between weather refreshes (providers update about every 15 minutes).</summary>
    public int WeatherIntervalMinutes { get; set; } = 15;

    /// <summary>Minutes between weather alert checks.</summary>
    public int AlertIntervalMinutes { get; set; } = 10;
}

/// <summary>
/// Refreshes weather, alerts and daily summaries on a schedule, and right away when the location or plugin
/// states change.
/// </summary>
public sealed class SiteBackgroundService(
    SiteService site,
    SiteSettingsStore settings,
    PluginCatalog plugins,
    IOptions<SiteOptions> options,
    TimeProvider time,
    ILogger<SiteBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);
    private DateTimeOffset _nextWeather = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAlerts = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSummaries = DateTimeOffset.MinValue;
    private int _weatherFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Wake()
        {
            _nextWeather = _nextAlerts = _nextSummaries = DateTimeOffset.MinValue;
            _wake.Release();
        }
        settings.LocationChanged += Wake;
        plugins.Changed += Wake;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                if (now >= _nextWeather)
                {
                    _nextWeather = now.AddMinutes(Math.Max(5, options.Value.WeatherIntervalMinutes));
                    await RunAsync("weather", site.RefreshWeatherAsync, stoppingToken);
                }
                if (now >= _nextAlerts)
                {
                    _nextAlerts = now.AddMinutes(Math.Max(5, options.Value.AlertIntervalMinutes));
                    await RunAsync("alerts", site.RefreshAlertsAsync, stoppingToken);
                }
                if (now >= _nextSummaries)
                {
                    _nextSummaries = now.AddMinutes(15);
                    await RunAsync("daily summaries", site.UpdateDaySummariesAsync, stoppingToken);
                }

                // Sleep until the next job is due (at most a minute), or until woken by a settings change.
                await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            settings.LocationChanged -= Wake;
            plugins.Changed -= Wake;
        }
    }

    private async Task RunAsync(string what, Func<CancellationToken, Task> job, CancellationToken ct)
    {
        try
        {
            await job(ct);
            if (what == "weather" && _weatherFailures > 0)
            {
                logger.LogInformation("Weather refresh recovered after {Failures} failure(s).", _weatherFailures);
                _weatherFailures = 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network services fail now and then; log the first failure fully, then occasionally.
            var failures = what == "weather" ? ++_weatherFailures : 1;
            if (failures == 1) logger.LogWarning(ex, "Refreshing {What} failed.", what);
            else if (failures % 12 == 0) logger.LogWarning("Refreshing {What} has failed {Failures} times: {Message}", what, failures, ex.Message);
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}

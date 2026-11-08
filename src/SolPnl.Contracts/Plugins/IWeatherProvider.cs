using SolPnl.Contracts.Models;

namespace SolPnl.Contracts.Plugins;

/// <summary>
/// A service plugin that supplies current conditions and forecasts for a location. SolPnl polls it on a schedule and
/// logs conditions alongside device readings. (Sensors on the network are device plugins, not weather providers.)
/// </summary>
public interface IWeatherProvider : IPlugin
{
    string Description { get; }

    Task<WeatherReport> GetWeatherAsync(SiteLocation location, CancellationToken cancellationToken = default);
}

/// <summary>Optional: a plugin that can turn a place name into coordinates (used by the location settings).</summary>
public interface ILocationSearch : IPlugin
{
    Task<IReadOnlyList<LocationSearchResult>> SearchLocationsAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>A service plugin that supplies official weather alerts for a location.</summary>
public interface IWeatherAlertProvider : IPlugin
{
    string Description { get; }

    Task<IReadOnlyList<WeatherAlert>> GetAlertsAsync(SiteLocation location, CancellationToken cancellationToken = default);
}

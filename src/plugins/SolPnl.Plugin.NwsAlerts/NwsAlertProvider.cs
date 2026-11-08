using System.Globalization;
using System.Net;
using System.Text.Json;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.NwsAlerts;

/// <summary>
/// Official watches, warnings and advisories from the US National Weather Service (api.weather.gov). Free, no key,
/// US locations only. NWS asks clients to identify themselves with a User-Agent.
/// </summary>
public sealed class NwsAlertProvider(HttpClient http) : IWeatherAlertProvider
{
    public const string PluginId = "nws-alerts";
    public const string UserAgent = "SolPnl (https://github.com/JosephAbreu/solpnl)";

    public string Id => PluginId;
    public string DisplayName => "US weather alerts (NWS)";

    public string Description =>
        "Official watches, warnings and advisories for your location from the US National Weather Service " +
        "(api.weather.gov). US locations only.";

    public async Task<IReadOnlyList<WeatherAlert>> GetAlertsAsync(SiteLocation location, CancellationToken cancellationToken = default)
    {
        var point = string.Create(CultureInfo.InvariantCulture, $"{location.Latitude:0.####},{location.Longitude:0.####}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.weather.gov/alerts/active?point={point}");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/geo+json");

        using var response = await http.SendAsync(request, cancellationToken);
        // Points outside the US (or not covered by NWS) are rejected; that simply means no NWS alerts apply.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return Parse(json.RootElement);
    }

    public static IReadOnlyList<WeatherAlert> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features)) return [];
        var alerts = new List<WeatherAlert>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var p)) continue;
            var id = Str(p, "id") ?? Str(feature, "id");
            var ev = Str(p, "event");
            if (id is null || ev is null) continue;

            alerts.Add(new WeatherAlert(
                id,
                ev,
                Str(p, "headline"),
                Str(p, "severity") ?? "Unknown",
                Str(p, "urgency"),
                Time(p, "effective") ?? Time(p, "onset"),
                Time(p, "ends") ?? Time(p, "expires"),
                Str(p, "description"),
                Str(p, "instruction"),
                Str(p, "senderName")));
        }

        // Most severe first.
        return alerts.OrderBy(a => SeverityRank(a.Severity)).ThenBy(a => a.Expires).ToList();
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Extreme" => 0,
        "Severe" => 1,
        "Moderate" => 2,
        "Minor" => 3,
        _ => 4,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}

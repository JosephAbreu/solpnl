using System.Globalization;
using SolPnl.Contracts.Models;
using SolPnl.Core.Site;

namespace SolPnl.Api;

/// <summary>Location and unit settings, the site overview (sun, weather, insights, alerts) and daily summaries.</summary>
public static class SiteEndpoints
{
    public static void MapSiteEndpoints(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings");

        settings.MapGet("", async (SiteSettingsStore store, CancellationToken ct) =>
                new SiteSettings(await store.GetLocationAsync(ct), await store.GetTemperatureUnitAsync(ct)))
            .WithName("GetSettings");

        settings.MapPut("/location", async (SiteLocation location, SiteSettingsStore store, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await store.SetLocationAsync(location, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("SetLocation");

        settings.MapPut("/units", async (SetUnitsRequest request, SiteSettingsStore store, CancellationToken ct) =>
        {
            try
            {
                await store.SetTemperatureUnitAsync(request.TemperatureUnit, ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("SetUnits");

        // Place search for the location settings (needs an enabled weather plugin that supports it, e.g. Open-Meteo).
        settings.MapGet("/location/search", async (string q, PluginCatalog catalog, CancellationToken ct) =>
        {
            if (await catalog.LocationSearchAsync(ct) is not { } search)
                return Results.Json(new ApiError("Place search needs the Open-Meteo plugin to be enabled. You can still enter coordinates."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            try
            {
                return Results.Ok(await search.SearchLocationsAsync(q, ct));
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new ApiError($"Place search failed: {ex.Message}"), statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("SearchLocations");

        var site = api.MapGroup("/site");

        site.MapGet("/overview", (SiteService service, CancellationToken ct) => service.GetOverviewAsync(ct))
            .WithName("GetSiteOverview");

        // Daily summaries, oldest first. from/to are local dates (yyyy-MM-dd); default: the last 30 days.
        site.MapGet("/days", async (string? from, string? to, DaySummaryStore days, SiteSettingsStore settingsStore,
            SavingsService savings, TimeProvider time, CancellationToken ct) =>
        {
            var location = await settingsStore.GetLocationAsync(ct);
            var zone = location is null ? TimeZoneInfo.Local : SiteSettingsStore.Zone(location);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);

            if (!TryDate(to, today, out var end) || !TryDate(from, end.AddDays(-29), out var start))
                return Results.BadRequest(new ApiError("'from' and 'to' must be dates like 2026-11-07."));
            if (start > end) return Results.BadRequest(new ApiError("'from' must not be after 'to'."));
            if (end.DayNumber - start.DayNumber > 3660) return Results.BadRequest(new ApiError("Ask for at most 10 years at a time."));

            return Results.Ok(await savings.WithSavingsAsync(await days.GetRangeAsync(start, end, ct), ct));
        }).WithName("GetDaySummaries");

        // Money saved by solar, and the electricity price it's based on.
        site.MapGet("/savings", (SavingsService savings, CancellationToken ct) => savings.GetSummaryAsync(ct))
            .WithName("GetSavings");

        api.MapGet("/settings/savings", (SavingsService savings, CancellationToken ct) => savings.GetSettingsAsync(ct))
            .WithName("GetSavingsSettings");

        api.MapPut("/settings/savings", async (SavingsSettings settings, SavingsService savings, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await savings.SaveSettingsAsync(settings, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("SaveSavingsSettings");
    }

    private static bool TryDate(string? value, DateOnly fallback, out DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = fallback;
            return true;
        }
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}

using System.Globalization;
using SolPnl.Contracts.Models;
using SolPnl.Core.Export;
using SolPnl.Core.Site;
using SolPnl.Core.Storage;

namespace SolPnl.Api;

/// <summary>CSV downloads: daily summaries and device readings.</summary>
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this RouteGroupBuilder api)
    {
        var export = api.MapGroup("/export");

        // Daily summaries for local dates [from, to] (yyyy-MM-dd); default: everything recorded.
        export.MapGet("/days.csv", async (string? from, string? to, DaySummaryStore days, SiteSettingsStore settingsStore,
            SavingsService savings, TimeProvider time, HttpContext http, CancellationToken ct) =>
        {
            var zone = await ZoneAsync(settingsStore, ct);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
            if (!SiteEndpoints.TryDate(to, today, out var end) || !SiteEndpoints.TryDate(from, DateOnly.MinValue, out var start))
                return Results.BadRequest(new ApiError("'from' and 'to' must be dates like 2026-11-07."));
            if (end > today) end = today;
            if (start > end) return Results.BadRequest(new ApiError("'from' must not be after 'to'."));

            var list = await savings.WithSavingsAsync(await days.GetRangeAsync(start, end, ct), ct);
            var settings = await savings.GetSettingsAsync(ct);
            var first = list.Count > 0 ? list[0].Date : start == DateOnly.MinValue ? end : start;
            var name = $"solpnl-days-{first:yyyyMMdd}-{end:yyyyMMdd}.csv";
            return Csv(http, name, writer => CsvExport.WriteDaysAsync(writer, list, zone,
                settings.RateList.Count > 0 ? settings.Currency : null));
        }).WithName("ExportDays");

        // A device's readings in [from, to] (default: the last 24 hours), one column per metric (default: all it
        // reports; repeat metric= to choose). resolution: raw | minute | hour | auto.
        export.MapGet("/readings.csv", async (string device, DateTimeOffset? from, DateTimeOffset? to, string? resolution,
            string[]? metric, IReadingStore store, SiteSettingsStore settingsStore, TimeProvider time, HttpContext http,
            CancellationToken ct) =>
        {
            var end = to ?? time.GetUtcNow();
            var start = from ?? end.AddHours(-24);
            if (start > end) return Results.BadRequest(new ApiError("'from' must be before 'to'."));

            var chosen = CsvExport.AutoResolution(end - start);
            if (!string.IsNullOrEmpty(resolution) && !resolution.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<HistoryResolution>(resolution, ignoreCase: true, out chosen) || !Enum.IsDefined(chosen))
                    return Results.BadRequest(new ApiError("'resolution' must be raw, minute, hour or auto."));
            }
            if (end - start > CsvExport.MaxSpan(chosen))
            {
                return Results.BadRequest(new ApiError(
                    $"That's too long for {chosen.ToString().ToLowerInvariant()} data: ask for at most {CsvExport.MaxSpan(chosen).TotalDays:0} days, or a coarser resolution."));
            }

            var latest = await store.GetLatestAsync(device, ct);
            if (latest.Count == 0) return Results.NotFound(new ApiError("No readings for that device."));
            var wanted = metric is { Length: > 0 } ? metric.ToHashSet(StringComparer.Ordinal) : null;
            var metrics = latest
                .Where(r => wanted is null || wanted.Contains(r.Metric))
                .OrderBy(r => r.Metric, StringComparer.Ordinal)
                .Select(r => (r.Metric, (string?)r.Unit))
                .ToList();
            if (metrics.Count == 0) return Results.BadRequest(new ApiError("The device doesn't report those metrics."));

            var zone = await ZoneAsync(settingsStore, ct);
            var localStart = TimeZoneInfo.ConvertTime(start, zone);
            var localEnd = TimeZoneInfo.ConvertTime(end, zone);
            var safeDevice = string.Concat(device.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
            var name = $"solpnl-{safeDevice}-{localStart:yyyyMMdd-HHmm}-{localEnd:yyyyMMdd-HHmm}-{chosen.ToString().ToLowerInvariant()}.csv";
            return Csv(http, name, writer =>
                CsvExport.WriteReadingsAsync(writer, store, device, metrics, start, end, chosen, zone, ct));
        }).WithName("ExportReadings");
    }

    private static async Task<TimeZoneInfo> ZoneAsync(SiteSettingsStore settings, CancellationToken ct) =>
        await settings.GetLocationAsync(ct) is { } location ? SiteSettingsStore.Zone(location) : TimeZoneInfo.Local;

    /// <summary>Streams a CSV attachment straight to the response.</summary>
    private static IResult Csv(HttpContext http, string fileName, Func<TextWriter, Task> write) =>
        Results.Stream(async body =>
        {
            await using var writer = new StreamWriter(body, CsvExport.Encoding, bufferSize: 64 * 1024);
            await write(writer);
        }, "text/csv; charset=utf-8", fileName);
}

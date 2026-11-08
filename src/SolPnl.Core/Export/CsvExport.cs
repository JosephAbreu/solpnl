using System.Globalization;
using System.Text;
using SolPnl.Contracts.Models;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Export;

/// <summary>
/// CSV downloads for spreadsheets: daily summaries, and a device's readings with one column per metric. Numbers use
/// "." decimals with no thousands separators; times are given in the site's time zone and in UTC.
/// </summary>
public static class CsvExport
{
    /// <summary>The most readings an export may span, by resolution, so a download stays a reasonable size.</summary>
    public static TimeSpan MaxSpan(HistoryResolution resolution) => resolution switch
    {
        HistoryResolution.Raw => TimeSpan.FromDays(7),
        HistoryResolution.Minute => TimeSpan.FromDays(92),
        _ => TimeSpan.FromDays(3660),
    };

    /// <summary>Readings are fetched a window at a time, so memory stays small however long the range.</summary>
    private static TimeSpan Window(HistoryResolution resolution) => resolution switch
    {
        HistoryResolution.Raw => TimeSpan.FromHours(6),
        HistoryResolution.Minute => TimeSpan.FromDays(3),
        _ => TimeSpan.FromDays(60),
    };

    /// <summary>The resolution an export picks when none is given: as the charts do.</summary>
    public static HistoryResolution AutoResolution(TimeSpan span) =>
        span <= TimeSpan.FromHours(2) ? HistoryResolution.Raw
        : span <= TimeSpan.FromDays(3) ? HistoryResolution.Minute
        : HistoryResolution.Hour;

    public static async Task WriteDaysAsync(TextWriter writer, IReadOnlyList<DaySummary> days, TimeZoneInfo zone, string? currency)
    {
        await writer.WriteLineAsync(Row(
            "date", "sunrise", "sunset",
            "forecast_high_c", "forecast_low_c", "forecast_sunlight_kwh_m2", "clear_sky_sunlight_kwh_m2",
            "actual_high_c", "actual_low_c",
            "solar_kwh", "home_use_kwh", "grid_kwh",
            currency is null ? "saved" : $"saved ({currency})"));
        foreach (var d in days)
        {
            await writer.WriteLineAsync(Row(
                d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                LocalTime(d.Sunrise, zone, "HH:mm"), LocalTime(d.Sunset, zone, "HH:mm"),
                Number(d.ForecastHighC), Number(d.ForecastLowC), Number(d.ForecastIrradiationKwhM2), Number(d.ClearSkyIrradiationKwhM2),
                Number(d.ActualHighC), Number(d.ActualLowC),
                Number(d.SolarKwh), Number(d.LoadKwh), Number(d.GridKwh), Number(d.Saved)));
        }
    }

    /// <summary>
    /// One row per timestamp, one column per metric ("pv.power (W)"); minute and hour rows hold the average. Rows are
    /// written in time order, a window at a time.
    /// </summary>
    public static async Task WriteReadingsAsync(TextWriter writer, IReadingStore store, string deviceId,
        IReadOnlyList<(string Metric, string? Unit)> metrics, DateTimeOffset from, DateTimeOffset to,
        HistoryResolution resolution, TimeZoneInfo zone, CancellationToken ct)
    {
        await writer.WriteLineAsync(Row(
            new[] { "time", "time_utc" }.Concat(metrics.Select(m => string.IsNullOrEmpty(m.Unit) ? m.Metric : $"{m.Metric} ({m.Unit})"))));

        var last = DateTimeOffset.MinValue;
        var window = Window(resolution);
        for (var start = from; start <= to; start += window)
        {
            var end = start + window < to ? start + window : to;
            var rows = new SortedDictionary<DateTimeOffset, double?[]>();
            for (var i = 0; i < metrics.Count; i++)
            {
                var history = await store.GetHistoryAsync(deviceId, metrics[i].Metric, start, end, resolution, ct);
                foreach (var p in history.Points)
                {
                    // Windows share their edges: skip a point already written.
                    if (p.Timestamp <= last || p.Timestamp < from || p.Timestamp > to) continue;
                    if (!rows.TryGetValue(p.Timestamp, out var values)) rows[p.Timestamp] = values = new double?[metrics.Count];
                    values[i] = p.Value;
                }
            }

            foreach (var (at, values) in rows)
            {
                await writer.WriteLineAsync(Row(
                    new[] { LocalTime(at, zone, "yyyy-MM-dd HH:mm:ss"), at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }
                        .Concat(values.Select(Number))));
                last = at;
            }
            if (end >= to) break;
        }
    }

    public static string Number(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("0.######", CultureInfo.InvariantCulture) : "";

    private static string LocalTime(DateTimeOffset? at, TimeZoneInfo zone, string format) =>
        at is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString(format, CultureInfo.InvariantCulture) : "";

    private static string Row(params string[] fields) => Row((IEnumerable<string>)fields);

    private static string Row(IEnumerable<string> fields) => string.Join(",", fields.Select(Escape));

    /// <summary>Quotes a field that holds a comma, quote or line break (RFC 4180), and defuses spreadsheet formulas.</summary>
    public static string Escape(string field)
    {
        if (field.Length > 0 && field[0] is '=' or '+' or '@' && !double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            field = "'" + field;
        return field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
    }

    /// <summary>UTF-8 with a byte order mark, so Excel reads "°C" and currency symbols correctly.</summary>
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
}

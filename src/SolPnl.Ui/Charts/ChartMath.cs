using System.Globalization;
using SolPnl.Contracts.Models;

namespace SolPnl.Ui.Charts;

/// <summary>One line on a <see cref="TimeSeriesChart"/>. <paramref name="Color"/> is a CSS color, e.g. "var(--series-1)".</summary>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<HistoryPoint> Points);

public readonly record struct Tick<T>(T Value, string Label);

/// <summary>Scale and layout arithmetic for charts, kept free of rendering so it can be unit-tested.</summary>
public static class ChartMath
{
    /// <summary>
    /// "Nice" linear ticks covering [min, max] with about <paramref name="target"/> intervals,
    /// using steps of 1, 2, 2.5 or 5 × 10^n.
    /// </summary>
    public static (double Low, double High, IReadOnlyList<double> Ticks) NiceScale(double min, double max, int target = 4)
    {
        if (double.IsNaN(min) || double.IsNaN(max)) (min, max) = (0, 1);
        if (max < min) (min, max) = (max, min);
        if (max - min < 1e-9)
        {
            var pad = Math.Abs(max) < 1e-9 ? 1 : Math.Abs(max) * 0.1;
            (min, max) = (min - pad, max + pad);
            if (min < 0 && max - pad >= 0) min = 0; // don't invent negatives for a flat non-negative series
        }

        var rough = (max - min) / Math.Max(1, target);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * magnitude).First(s => s >= rough * 0.999);

        var low = Math.Floor(min / step) * step;
        var high = Math.Ceiling(max / step) * step;
        var ticks = new List<double>();
        for (var v = low; v <= high + step * 1e-6; v += step)
            ticks.Add(Math.Round(v / step) * step);
        return (low, high, ticks);
    }

    private static readonly TimeSpan[] TimeSteps =
    [
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
    ];

    /// <summary>Time-axis ticks aligned to round local times, at most one per <paramref name="minSpacingPx"/>.</summary>
    public static IReadOnlyList<Tick<DateTimeOffset>> TimeTicks(
        DateTimeOffset from, DateTimeOffset to, double plotWidth, TimeZoneInfo zone, CultureInfo culture, double minSpacingPx = 90)
    {
        var span = to - from;
        if (span <= TimeSpan.Zero || plotWidth <= 0) return [];

        var maxTicks = Math.Max(2, (int)(plotWidth / minSpacingPx));
        var step = TimeSteps.FirstOrDefault(s => span / s <= maxTicks);
        if (step == default) step = TimeSpan.FromDays(Math.Ceiling(span.TotalDays / maxTicks));

        var localFrom = TimeZoneInfo.ConvertTime(from, zone);
        DateTime first;
        if (step < TimeSpan.FromDays(1))
        {
            var midnight = localFrom.Date;
            var intoDay = localFrom.DateTime - midnight;
            first = midnight + TimeSpan.FromTicks((long)Math.Ceiling(intoDay.Ticks / (double)step.Ticks) * step.Ticks);
        }
        else
        {
            first = localFrom.DateTime == localFrom.Date ? localFrom.Date : localFrom.Date.AddDays(1);
        }

        var ticks = new List<Tick<DateTimeOffset>>();
        var multiDay = span > TimeSpan.FromDays(1);
        for (var local = first; ; local += step)
        {
            var at = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (at > to) break;
            if (at < from) continue;

            var label = step >= TimeSpan.FromDays(1) || (multiDay && local.TimeOfDay == TimeSpan.Zero)
                ? local.ToString("MMM d", culture)
                : local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
            ticks.Add(new(at, label));
        }
        return ticks;
    }

    /// <summary>
    /// Splits points into contiguous runs, breaking where the gap exceeds three typical intervals,
    /// so an offline device shows as a gap instead of a misleading straight line.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<HistoryPoint>> Segments(IReadOnlyList<HistoryPoint> points)
    {
        if (points.Count == 0) return [];
        var threshold = TypicalInterval(points) * 3;

        var segments = new List<IReadOnlyList<HistoryPoint>>();
        var current = new List<HistoryPoint> { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Timestamp - points[i - 1].Timestamp > threshold)
            {
                segments.Add(current);
                current = [];
            }
            current.Add(points[i]);
        }
        segments.Add(current);
        return segments;
    }

    /// <summary>Median spacing between consecutive points (1 minute when there are fewer than two).</summary>
    public static TimeSpan TypicalInterval(IReadOnlyList<HistoryPoint> points)
    {
        if (points.Count < 2) return TimeSpan.FromMinutes(1);
        var gaps = new List<long>(points.Count - 1);
        for (var i = 1; i < points.Count; i++) gaps.Add((points[i].Timestamp - points[i - 1].Timestamp).Ticks);
        gaps.Sort();
        return TimeSpan.FromTicks(Math.Max(1, gaps[gaps.Count / 2]));
    }

    /// <summary>Index of the point nearest to <paramref name="at"/> in time-ordered points, or -1 if empty.</summary>
    public static int Nearest(IReadOnlyList<HistoryPoint> points, DateTimeOffset at)
    {
        if (points.Count == 0) return -1;
        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].Timestamp < at) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && at - points[lo - 1].Timestamp < points[lo].Timestamp - at) lo--;
        return lo;
    }
}

/// <summary>A metric to plot: which metric, its legend name, and its fixed series color (color follows the entity).</summary>
public sealed record SeriesSpec(string Metric, string Name, string Color);

/// <summary>Fixed categorical slots, assigned in order and never cycled.</summary>
public static class SeriesColors
{
    public const string Slot1 = "var(--series-1)";
    public const string Slot2 = "var(--series-2)";
    public const string Slot3 = "var(--series-3)";
}

using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SolPnl.Contracts.Models;

namespace SolPnl.Ui.Charts;

/// <summary>
/// Dependency-free SVG line chart for time series: 2px lines, recessive hairline grid, end value labels when they
/// don't collide, gaps where data is missing, optional min/max band (single series), and a crosshair tooltip that
/// lists every series at the hovered time (pointer or arrow keys).
/// </summary>
public partial class TimeSeriesChart
{
    [Parameter, EditorRequired] public IReadOnlyList<ChartSeries> Series { get; set; } = [];
    [Parameter, EditorRequired] public DateTimeOffset From { get; set; }
    [Parameter, EditorRequired] public DateTimeOffset To { get; set; }
    [Parameter] public string? Unit { get; set; }
    [Parameter] public HistoryResolution Resolution { get; set; }
    [Parameter] public bool ZeroBaseline { get; set; } = true;
    [Parameter] public int Height { get; set; } = 240;
    [Parameter] public bool Loading { get; set; }
    [Parameter] public string AriaLabel { get; set; } = "Chart";

    private const double MarginTop = 12, MarginBottom = 26, MarginLeft = 52;
    private const double EndLabelSpace = 76, MinEndLabelGapPx = 14;

    private ElementReference _container;
    private IJSObjectReference? _module;
    private DotNetObjectReference<TimeSeriesChart>? _self;
    private double _width;
    private Layout? _layout;
    private HoverState? _hover;
    private int _keyboardIndex = -1;

    protected override void OnParametersSet()
    {
        _layout = _width > 0 ? BuildLayout() : null;
        _hover = null;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _self = DotNetObjectReference.Create(this);
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/SolPnl.Ui/solpnl.js");
        await _module.InvokeVoidAsync("observeWidth", _container, _self);
    }

    [JSInvokable]
    public void OnWidthChanged(double width)
    {
        _width = width;
        _layout = BuildLayout();
        _hover = null;
        StateHasChanged();
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private Layout BuildLayout()
    {
        var all = Series.SelectMany(s => s.Points).ToList();
        var dataMin = all.Count > 0 ? all.Min(p => p.Value) : 0;
        var dataMax = all.Count > 0 ? all.Max(p => p.Value) : 1;
        if (Series.Count == 1)
        {
            dataMin = Math.Min(dataMin, all.Where(p => p.Min.HasValue).Select(p => p.Min!.Value).DefaultIfEmpty(dataMin).Min());
            dataMax = Math.Max(dataMax, all.Where(p => p.Max.HasValue).Select(p => p.Max!.Value).DefaultIfEmpty(dataMax).Max());
        }
        if (ZeroBaseline) dataMin = Math.Min(0, dataMin);
        var (yLow, yHigh, yTicks) = ChartMath.NiceScale(dataMin, dataMax);

        var plotHeight = Height - MarginTop - MarginBottom;
        double Y(double v) => MarginTop + plotHeight - (v - yLow) / (yHigh - yLow) * plotHeight;

        // Show end labels only when there's room and they don't collide.
        var ends = Series.Where(s => s.Points.Count > 0).Select(s => Y(s.Points[^1].Value)).OrderBy(y => y).ToList();
        var showEndLabels = _width >= 480 && ends.Count > 0 &&
                            ends.Zip(ends.Skip(1), (a, b) => b - a).All(gap => gap >= MinEndLabelGapPx);

        var plotWidth = Math.Max(10, _width - MarginLeft - (showEndLabels ? EndLabelSpace : 16));
        var spanTicks = Math.Max(1, (To - From).Ticks);
        double X(DateTimeOffset t) => MarginLeft + (t - From).Ticks / (double)spanTicks * plotWidth;

        var lines = Series.Select(s => BuildLine(s, X, Y)).ToList();
        string? band = Series.Count == 1 ? BuildBand(Series[0].Points, X, Y) : null;

        var yStep = yTicks.Count > 1 ? yTicks[1] - yTicks[0] : 1;
        return new Layout(MarginLeft, MarginTop, plotWidth, plotHeight, yLow, yHigh, yStep, yTicks,
            ChartMath.TimeTicks(From, To, plotWidth, TimeZoneInfo.Local, CultureInfo.CurrentCulture),
            lines, band, showEndLabels, all.Count == 0, X, Y);
    }

    private static Line BuildLine(ChartSeries s, Func<DateTimeOffset, double> x, Func<double, double> y)
    {
        var paths = new List<string>();
        var lone = new List<(double, double)>();
        foreach (var segment in ChartMath.Segments(s.Points))
        {
            if (segment.Count == 1)
            {
                lone.Add((x(segment[0].Timestamp), y(segment[0].Value)));
                continue;
            }
            var sb = new StringBuilder();
            for (var i = 0; i < segment.Count; i++)
                sb.Append(i == 0 ? 'M' : 'L').Append(F(x(segment[i].Timestamp))).Append(' ').Append(F(y(segment[i].Value))).Append(' ');
            paths.Add(sb.ToString());
        }

        EndPoint? end = s.Points.Count > 0
            ? new EndPoint(x(s.Points[^1].Timestamp), y(s.Points[^1].Value), s.Points[^1].Value)
            : null;
        // A lone final point is already drawn as the end dot.
        if (end is not null && lone.Count > 0 && lone[^1] == (end.X, end.Y)) lone.RemoveAt(lone.Count - 1);
        return new Line(s.Color, paths, lone, end);
    }

    private static string? BuildBand(IReadOnlyList<HistoryPoint> points, Func<DateTimeOffset, double> x, Func<double, double> y)
    {
        var sb = new StringBuilder();
        foreach (var segment in ChartMath.Segments(points))
        {
            if (segment.Count < 2 || segment.Any(p => p.Min is null || p.Max is null)) continue;
            for (var i = 0; i < segment.Count; i++)
                sb.Append(i == 0 ? 'M' : 'L').Append(F(x(segment[i].Timestamp))).Append(' ').Append(F(y(segment[i].Max!.Value))).Append(' ');
            for (var i = segment.Count - 1; i >= 0; i--)
                sb.Append('L').Append(F(x(segment[i].Timestamp))).Append(' ').Append(F(y(segment[i].Min!.Value))).Append(' ');
            sb.Append("Z ");
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private void OnPointerMove(PointerEventArgs e)
    {
        if (_layout is not { } l) return;
        var fraction = Math.Clamp(e.OffsetX / l.PlotWidth, 0, 1);
        ShowHoverAt(From + TimeSpan.FromTicks((long)((To - From).Ticks * fraction)));
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        var primary = Series.OrderByDescending(s => s.Points.Count).FirstOrDefault();
        if (primary is null || primary.Points.Count == 0) return;

        _keyboardIndex = e.Key switch
        {
            "ArrowRight" => Math.Min(primary.Points.Count - 1, _keyboardIndex < 0 ? primary.Points.Count - 1 : _keyboardIndex + 1),
            "ArrowLeft" => Math.Max(0, _keyboardIndex < 0 ? primary.Points.Count - 1 : _keyboardIndex - 1),
            "Home" => 0,
            "End" => primary.Points.Count - 1,
            "Escape" => -1,
            _ => _keyboardIndex,
        };
        if (_keyboardIndex < 0) ClearHover();
        else ShowHoverAt(primary.Points[_keyboardIndex].Timestamp);
    }

    private void ShowHoverAt(DateTimeOffset at)
    {
        if (_layout is not { } l) return;

        var rows = new List<HoverRow>();
        foreach (var s in Series)
        {
            var i = ChartMath.Nearest(s.Points, at);
            if (i < 0) continue;
            var p = s.Points[i];
            // Only report a series if it actually has data near this time (not across a gap).
            if ((p.Timestamp - at).Duration() > ChartMath.TypicalInterval(s.Points) * 2) continue;
            rows.Add(new HoverRow(s.Name, s.Color, p, l.X(p.Timestamp), l.Y(p.Value)));
        }

        if (rows.Count == 0)
        {
            _hover = null;
            return;
        }

        // Snap the crosshair to the data point nearest the pointer.
        var snapped = rows.MinBy(r => (r.Point.Timestamp - at).Duration())!.Point.Timestamp;
        _hover = new HoverState(l.X(snapped), TimeLabel(snapped), rows);
    }

    private void ClearHover()
    {
        _hover = null;
        _keyboardIndex = -1;
    }

    private string TimeLabel(DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local);
        var culture = CultureInfo.CurrentCulture;
        return Resolution switch
        {
            HistoryResolution.Hour => $"{local.ToString("MMM d", culture)}, {local.ToString(culture.DateTimeFormat.ShortTimePattern, culture)} (hour)",
            HistoryResolution.Minute => $"{local.ToString("MMM d", culture)}, {local.ToString(culture.DateTimeFormat.ShortTimePattern, culture)}",
            _ => $"{local.ToString("MMM d", culture)}, {local.ToString(culture.DateTimeFormat.LongTimePattern, culture)}",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("unobserve", _container);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
        _self?.Dispose();
    }

    private sealed record Layout(
        double Left, double Top, double PlotWidth, double PlotHeight,
        double YLow, double YHigh, double YStep, IReadOnlyList<double> YTicks, IReadOnlyList<Tick<DateTimeOffset>> XTicks,
        IReadOnlyList<Line> Lines, string? Band, bool ShowEndLabels, bool IsEmpty,
        Func<DateTimeOffset, double> X, Func<double, double> Y);

    private sealed record Line(string Color, IReadOnlyList<string> Paths, IReadOnlyList<(double X, double Y)> LonePoints, EndPoint? End);

    private sealed record EndPoint(double X, double Y, double Value);

    private sealed record HoverRow(string Name, string Color, HistoryPoint Point, double X, double Y);

    private sealed record HoverState(double X, string TimeLabel, IReadOnlyList<HoverRow> Rows);
}

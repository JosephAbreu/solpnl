using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace SolPnl.Ui.Charts;

/// <summary>One series of a <see cref="BarChart"/>: a value (or null, no bar) per category.</summary>
public sealed record BarSeries(string Name, string Color, IReadOnlyList<double?> Values);

/// <summary>
/// Dependency-free SVG bar chart: one group of bars per category, side by side for each series, on a zero baseline.
/// Thin bars with 4px rounded tops, a 2px gap between bars in a group, hairline grid, x labels thinned so they don't
/// collide, and a tooltip listing every series for the hovered category (pointer or arrow keys). With
/// <see cref="OnSelect"/>, clicking a category (or Enter) reports it, e.g. to drill into a month.
/// </summary>
public partial class BarChart
{
    [Parameter, EditorRequired] public IReadOnlyList<string> Categories { get; set; } = [];
    [Parameter, EditorRequired] public IReadOnlyList<BarSeries> Series { get; set; } = [];
    [Parameter] public Func<double, string>? Format { get; set; }
    [Parameter] public Func<int, string>? TooltipTitle { get; set; }
    [Parameter] public EventCallback<int> OnSelect { get; set; }
    [Parameter] public int Height { get; set; } = 260;
    [Parameter] public bool Loading { get; set; }
    [Parameter] public string AriaLabel { get; set; } = "Chart";
    [Parameter] public string EmptyText { get; set; } = "No data for this period yet";

    private const double MarginTop = 12, MarginBottom = 26, MarginLeft = 52, MarginRight = 4;
    private const double BarGap = 2, MinLabelSpacingPx = 34, MaxBarWidth = 28, Radius = 4;

    private ElementReference _container;
    private IJSObjectReference? _module;
    private DotNetObjectReference<BarChart>? _self;
    private double _width;
    private Layout? _layout;
    private int? _hover;

    private string FormatValue(double v) => Format?.Invoke(v) ?? v.ToString("N1", CultureInfo.CurrentCulture);

    protected override void OnParametersSet()
    {
        _layout = _width > 0 ? BuildLayout() : null;
        if (_hover >= Categories.Count) _hover = null;
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
        StateHasChanged();
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private Layout BuildLayout()
    {
        var values = Series.SelectMany(s => s.Values).Where(v => v is not null).Select(v => v!.Value).ToList();
        var max = values.Count > 0 ? Math.Max(0, values.Max()) : 0;
        var (_, yHigh, yTicks) = ChartMath.NiceScale(0, max <= 0 ? 1 : max);
        var yStep = yTicks.Count > 1 ? yTicks[1] - yTicks[0] : 1;

        var plotWidth = Math.Max(10, _width - MarginLeft - MarginRight);
        var plotHeight = Height - MarginTop - MarginBottom;
        var n = Math.Max(1, Categories.Count);
        var groupWidth = plotWidth / n;
        double Y(double v) => MarginTop + plotHeight - v / yHigh * plotHeight;

        // Bars take ~70% of the group (less for many series), capped so a few bars don't turn into slabs.
        var seriesCount = Math.Max(1, Series.Count);
        var inner = groupWidth * (n > 20 ? 0.8 : 0.7);
        var barWidth = Math.Clamp((inner - BarGap * (seriesCount - 1)) / seriesCount, 1, MaxBarWidth);
        var used = barWidth * seriesCount + BarGap * (seriesCount - 1);

        var bars = new List<Bar>();
        for (var i = 0; i < Categories.Count; i++)
        {
            var x0 = MarginLeft + i * groupWidth + (groupWidth - used) / 2;
            for (var s = 0; s < Series.Count; s++)
            {
                if (i >= Series[s].Values.Count || Series[s].Values[i] is not { } v || v <= 0) continue;
                var x = x0 + s * (barWidth + BarGap);
                bars.Add(new Bar(BarPath(x, Y(v), barWidth, Y(0)), Series[s].Color));
            }
        }

        var labelEvery = Math.Max(1, (int)Math.Ceiling(MinLabelSpacingPx / groupWidth));
        return new Layout(MarginLeft, MarginTop, plotWidth, plotHeight, groupWidth, yTicks, yStep, Y, bars, labelEvery,
            values.Count == 0);
    }

    /// <summary>A bar with rounded top corners, anchored square to the baseline.</summary>
    private static string BarPath(double x, double top, double width, double baseline)
    {
        var r = Math.Min(Radius, Math.Min(width / 2, baseline - top));
        return FormattableString.Invariant(
            $"M{x:0.##},{baseline:0.##}V{top + r:0.##}Q{x:0.##},{top:0.##} {x + r:0.##},{top:0.##}H{x + width - r:0.##}Q{x + width:0.##},{top:0.##} {x + width:0.##},{top + r:0.##}V{baseline:0.##}Z");
    }

    private void OnPointerMove(PointerEventArgs e)
    {
        if (_layout is not { } l) return;
        var i = (int)Math.Floor(e.OffsetX / l.GroupWidth);
        _hover = Math.Clamp(i, 0, Categories.Count - 1);
    }

    private void ClearHover() => _hover = null;

    private async Task OnClick()
    {
        if (_hover is { } i && OnSelect.HasDelegate) await OnSelect.InvokeAsync(i);
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (Categories.Count == 0) return;
        var last = Categories.Count - 1;
        switch (e.Key)
        {
            case "ArrowRight": _hover = _hover is { } r ? Math.Min(last, r + 1) : 0; break;
            case "ArrowLeft": _hover = _hover is { } l ? Math.Max(0, l - 1) : last; break;
            case "Home": _hover = 0; break;
            case "End": _hover = last; break;
            case "Escape": _hover = null; break;
            case "Enter" or " ": await OnClick(); break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("unobserve", _container);
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        _self?.Dispose();
    }

    private sealed record Bar(string Path, string Color);

    private sealed record Layout(
        double Left, double Top, double PlotWidth, double PlotHeight, double GroupWidth,
        IReadOnlyList<double> YTicks, double YStep, Func<double, double> Y, IReadOnlyList<Bar> Bars, int LabelEvery,
        bool IsEmpty)
    {
        public double GroupX(int i) => Left + i * GroupWidth;
    }
}

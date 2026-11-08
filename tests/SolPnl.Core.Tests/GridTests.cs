using SolPnl.Contracts.Models;
using SolPnl.Core.Grid;
using SolPnl.Core.Notifications;

namespace SolPnl.Core.Tests;

public class GridTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Samples every 10 s from <paramref name="start"/> seconds, both legs at <paramref name="volts"/>.</summary>
    private static IEnumerable<GridSample> Every10s(int start, int count, double volts, double? leg2 = null) =>
        Enumerable.Range(0, count).Select(i => new GridSample(T0.AddSeconds(start + i * 10), [volts, leg2 ?? volts]));

    [Fact]
    public void Outage_NeedsTwentySeconds_AndEndsWhenVoltageReturns()
    {
        var tracker = new GridTracker();
        Assert.Empty(tracker.Process(Every10s(0, 3, 123.5), T0.AddSeconds(30), null, null));

        // A single bad sample is a blip, not an outage.
        Assert.Empty(tracker.Process(Every10s(30, 1, 0.4), T0.AddSeconds(40), null, null));
        Assert.Empty(tracker.Process(Every10s(40, 1, 123), T0.AddSeconds(50), null, null));

        var opened = tracker.Process(Every10s(50, 3, 0.2), T0.AddSeconds(80), null, null);
        var outage = Assert.Single(opened);
        Assert.Equal(GridEventKinds.Outage, outage.Kind);
        Assert.Equal(T0.AddSeconds(50), outage.StartedAt);
        Assert.Null(outage.EndedAt);

        // Device offline for an hour, then the grid is back: the outage spans the gap.
        var closed = tracker.Process(Every10s(3700, 2, 122.8), T0.AddSeconds(3720), null, null);
        Assert.Same(outage, Assert.Single(closed));
        Assert.Equal(T0.AddSeconds(3700), outage.EndedAt);
        Assert.Null(tracker.Outage);
    }

    [Fact]
    public void GridDropsThenInverterGoesQuiet_CountsAsOutage()
    {
        // At night with no battery the inverter shuts down with the grid: one zero reading, then silence.
        var tracker = new GridTracker();
        tracker.Process(Every10s(0, 3, 123), T0.AddSeconds(30), null, null);
        Assert.Empty(tracker.Process(Every10s(30, 1, 0), T0.AddSeconds(45), null, null));
        var outage = Assert.Single(tracker.Process([], T0.AddSeconds(95), null, null));
        Assert.Equal(T0.AddSeconds(30), outage.StartedAt);
    }

    [Fact]
    public void LowVoltage_AfterAMinute_TracksTheLowest_AndAutoLimitsFollowTheServiceVoltage()
    {
        var tracker = new GridTracker();
        // One leg sags to 104 V (below 106 V, automatic for 120 V service).
        Assert.Empty(tracker.Process(Every10s(0, 6, 104, leg2: 121), T0.AddSeconds(50), null, null));
        var low = Assert.Single(tracker.Process(Every10s(60, 2, 103.2, leg2: 121), T0.AddSeconds(70), null, null));
        Assert.Equal(GridEventKinds.Low, low.Kind);
        Assert.Equal(T0, low.StartedAt);
        Assert.Equal(103.2, low.Extreme);

        Assert.Single(tracker.Process(Every10s(80, 1, 118, leg2: 121), T0.AddSeconds(80), null, null));
        Assert.Equal(T0.AddSeconds(80), low.EndedAt);

        // 230 V service: 230 V is normal, not "high".
        var european = new GridTracker();
        Assert.Empty(european.Process(Every10s(0, 20, 231), T0.AddSeconds(200), null, null));
        // User limits override the automatic ones.
        var strict = new GridTracker();
        Assert.Equal(GridEventKinds.High, Assert.Single(strict.Process(Every10s(0, 8, 124), T0.AddSeconds(80), null, 123.5)).Kind);
    }

    [Fact]
    public void Outage_ReplacesAVoltageEvent()
    {
        var tracker = new GridTracker();
        var low = Assert.Single(tracker.Process(Every10s(0, 7, 100), T0.AddSeconds(60), null, null));
        var changes = tracker.Process(Every10s(70, 3, 0), T0.AddSeconds(90), null, null);
        Assert.Equal(2, changes.Count);
        Assert.Same(low, changes[0]);
        Assert.Equal(T0.AddSeconds(70), low.EndedAt);
        Assert.Equal(GridEventKinds.Outage, changes[1].Kind);
    }

    [Theory]
    [InlineData(42, "42 s")]
    [InlineData(600, "10 min")]
    [InlineData(7500, "2 h 5 min")]
    [InlineData(7200, "2 h")]
    [InlineData(97200, "1 d 3 h")]
    public void Durations_ReadNaturally(int seconds, string expected) =>
        Assert.Equal(expected, NotificationService.Duration(TimeSpan.FromSeconds(seconds)));
}

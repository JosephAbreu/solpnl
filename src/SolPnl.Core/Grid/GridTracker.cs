using SolPnl.Contracts.Models;

namespace SolPnl.Core.Grid;

/// <summary>One poll's grid voltages, one value per leg (or a single value).</summary>
public sealed record GridSample(DateTimeOffset At, IReadOnlyList<double> Legs);

/// <summary>Per-leg voltage limits in volts.</summary>
public sealed record GridLimits(double Low, double High)
{
    /// <summary>
    /// The user's limits, or automatic ones for the service voltage seen: ANSI C84.1 range B for 120 V service
    /// (106–127 V), ±10 % for 230 V.
    /// </summary>
    public static GridLimits For(double legVoltage, double? low, double? high)
    {
        var automatic = legVoltage > 160 ? new GridLimits(207, 253) : new GridLimits(106, 127);
        return new GridLimits(low ?? automatic.Low, high ?? automatic.High);
    }
}

/// <summary>A grid event being tracked: opened (and maybe closed) by <see cref="GridTracker"/>.</summary>
public sealed class TrackedGridEvent
{
    public long Id { get; set; }
    public required string Kind { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public double Extreme { get; set; }
}

/// <summary>
/// Turns one device's grid voltage samples into outage and low/high voltage events.
///
///  - Outage: every leg under <see cref="OutageVolts"/> for <see cref="OutageConfirm"/>; or under it and then the
///    device goes quiet (an inverter with no battery shuts down when the grid drops and there isn't enough sun).
///    Ends at the first sample with voltage back.
///  - Low / high: any leg outside the limits for <see cref="VoltageConfirm"/>; ends at the first sample inside them.
///    An outage replaces a low/high event.
/// A gap in samples (device offline) leaves open events open; they end when samples show the grid back.
/// </summary>
public sealed class GridTracker
{
    public const double OutageVolts = 30;
    public static readonly TimeSpan OutageConfirm = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan VoltageConfirm = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SilenceConfirm = TimeSpan.FromSeconds(60);

    private (DateTimeOffset Since, double Min)? _pendingOutage;
    private (string Kind, DateTimeOffset Since, double Extreme)? _pendingVoltage;
    private DateTimeOffset? _lastSample;

    public TrackedGridEvent? Outage { get; private set; }
    public TrackedGridEvent? Voltage { get; private set; }

    /// <summary>Picks up events still open from before (e.g. across a restart).</summary>
    public void Resume(TrackedGridEvent open)
    {
        if (open.Kind == GridEventKinds.Outage) Outage = open;
        else Voltage = open;
    }

    /// <summary>Processes samples (oldest first). Returns the events opened or closed, in order.</summary>
    public List<TrackedGridEvent> Process(IEnumerable<GridSample> samples, DateTimeOffset now, double? low, double? high)
    {
        var changed = new List<TrackedGridEvent>();
        foreach (var sample in samples)
        {
            if (sample.Legs.Count == 0) continue;
            var min = sample.Legs.Min();
            var max = sample.Legs.Max();
            _lastSample = sample.At;

            if (max < OutageVolts)
            {
                // No grid: any low/high event gives way to the outage.
                _pendingVoltage = null;
                if (Voltage is { } v) changed.Add(Close(v, sample.At, () => Voltage = null));

                if (Outage is { } outage)
                {
                    outage.Extreme = Math.Min(outage.Extreme, min);
                    continue;
                }
                _pendingOutage = _pendingOutage is { } p ? (p.Since, Math.Min(p.Min, min)) : (sample.At, min);
                if (sample.At - _pendingOutage.Value.Since >= OutageConfirm) changed.Add(OpenOutage());
                continue;
            }

            _pendingOutage = null;
            if (Outage is { } open) changed.Add(Close(open, sample.At, () => Outage = null));

            var limits = GridLimits.For(max, low, high);
            var kind = min < limits.Low ? GridEventKinds.Low : max > limits.High ? GridEventKinds.High : null;
            var extreme = kind == GridEventKinds.High ? max : min;

            if (Voltage is { } current)
            {
                if (current.Kind == kind)
                {
                    current.Extreme = kind == GridEventKinds.High ? Math.Max(current.Extreme, extreme) : Math.Min(current.Extreme, extreme);
                    continue;
                }
                changed.Add(Close(current, sample.At, () => Voltage = null));
            }

            if (kind is null)
            {
                _pendingVoltage = null;
                continue;
            }
            _pendingVoltage = _pendingVoltage is { } pv && pv.Kind == kind
                ? (kind, pv.Since, kind == GridEventKinds.High ? Math.Max(pv.Extreme, extreme) : Math.Min(pv.Extreme, extreme))
                : (kind, sample.At, extreme);
            if (sample.At - _pendingVoltage.Value.Since >= VoltageConfirm)
            {
                var (k, since, ext) = _pendingVoltage.Value;
                Voltage = new TrackedGridEvent { Kind = k, StartedAt = since, Extreme = ext };
                _pendingVoltage = null;
                changed.Add(Voltage);
            }
        }

        // The grid dropped and then the device went quiet: the inverter most likely shut down with it.
        if (_pendingOutage is not null && Outage is null && _lastSample is { } last && now - last >= SilenceConfirm)
            changed.Add(OpenOutage());

        return changed;
    }

    private TrackedGridEvent OpenOutage()
    {
        var (since, min) = _pendingOutage!.Value;
        _pendingOutage = null;
        Outage = new TrackedGridEvent { Kind = GridEventKinds.Outage, StartedAt = since, Extreme = min };
        return Outage;
    }

    private static TrackedGridEvent Close(TrackedGridEvent e, DateTimeOffset at, Action forget)
    {
        e.EndedAt = at;
        forget();
        return e;
    }
}

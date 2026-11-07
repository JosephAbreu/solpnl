using System.Globalization;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Simulator;

/// <summary>
/// A fake split-phase inverter with a solar curve and a varying load. For development and testing only: SolPnl
/// registers it in the Development environment, so the UI can be exercised without real hardware (and without
/// opening extra connections to a real logger that another SolPnl instance is already polling).
/// </summary>
public sealed class SimulatedInverterPluginType : IDevicePluginType
{
    public const string PluginId = "simulator";

    public string Id => PluginId;
    public string DisplayName => "Simulated inverter (development)";
    public string Description => "A fake split-phase inverter with a daytime solar curve. Development only.";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("name", "Simulator ID", Placeholder: "sim-1", Help: "Any short ID; it becomes part of the device ID."),
        new("peakWatts", "Peak solar (W)", SettingKind.Number, Required: false, Default: "6000"),
        new("failing", "Simulate offline (true/false)", Required: false, Default: "false"),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings)
    {
        var name = settings.TryGetValue("name", out var n) ? n?.Trim() : null;
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Simulator ID is required (letters, digits, '-' or '_').");

        var peak = 6000;
        if (settings.TryGetValue("peakWatts", out var p) && !string.IsNullOrWhiteSpace(p)
            && (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out peak) || peak <= 0))
            throw new ArgumentException("Peak solar must be a positive whole number of watts.");

        var failing = settings.TryGetValue("failing", out var f) && bool.TryParse(f, out var b) && b;
        return new SimulatedInverter($"sim-{name}", peak, failing);
    }
}

internal sealed class SimulatedInverter(string deviceId, int peakWatts, bool failing) : IDevicePlugin
{
    public string Id => SimulatedInverterPluginType.PluginId;
    public string DisplayName => "Simulated inverter";

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInfo(deviceId, "SolPnl", "Simulated inverter", deviceId, "sim"));

    public Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (failing) throw new IOException("Simulated device is offline.");

        var now = DateTimeOffset.UtcNow;
        var local = now.ToLocalTime();
        var hour = local.TimeOfDay.TotalHours;
        var daylight = Math.Max(0, Math.Sin((hour - 6) / 12 * Math.PI)); // 06:00..18:00
        var jitter = 0.9 + Random.Shared.NextDouble() * 0.2;
        var pv1 = Math.Round(peakWatts * 0.6 * daylight * jitter);
        var pv2 = Math.Round(peakWatts * 0.4 * daylight * jitter);
        var l1 = Math.Round(250 + Random.Shared.NextDouble() * 400);
        var l2 = Math.Round(300 + Random.Shared.NextDouble() * 700);

        Reading R(string metric, double value, string unit) => new(deviceId, metric, value, unit, now);
        IReadOnlyList<Reading> readings =
        [
            R(Metrics.ForString(Metrics.PvPower, 1), pv1, "W"),
            R(Metrics.ForString(Metrics.PvPower, 2), pv2, "W"),
            R(Metrics.PvPower, pv1 + pv2, "W"),
            R(Metrics.ForPhase(Metrics.LoadPower, 1), l1, "W"),
            R(Metrics.ForPhase(Metrics.LoadPower, 2), l2, "W"),
            R(Metrics.LoadPower, l1 + l2, "W"),
            R(Metrics.ForPhase(Metrics.GridVoltage, 1), Math.Round(121 + Random.Shared.NextDouble() * 2, 1), "V"),
            R(Metrics.ForPhase(Metrics.GridVoltage, 2), Math.Round(121 + Random.Shared.NextDouble() * 2, 1), "V"),
            R(Metrics.GridFrequency, Math.Round(59.98 + Random.Shared.NextDouble() * 0.04, 2), "Hz"),
            R(Metrics.InverterTemperature, Math.Round(30 + daylight * 15, 1), "°C"),
        ];
        return Task.FromResult(readings);
    }
}

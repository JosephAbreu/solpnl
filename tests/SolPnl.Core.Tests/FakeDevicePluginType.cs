using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Core.Tests;

/// <summary>Device type for tests: settings "id" (required), "fail" ("true" makes reads throw), "secret" (Secret).</summary>
internal sealed class FakeDevicePluginType(string pluginId = "fake") : IDevicePluginType
{
    public List<FakeDevice> Created { get; } = [];

    public string Id => pluginId;
    public string DisplayName => $"Fake ({pluginId})";
    public string Description => "Test device type.";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("id", "ID"),
        new("fail", "Fail", Required: false),
        new("secret", "Secret", SettingKind.Secret, Required: false),
    ];

    public IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings)
    {
        var id = settings.TryGetValue("id", out var v) ? v : null;
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("ID is required.");
        var device = new FakeDevice(Id, $"fake-{id}", settings.TryGetValue("fail", out var f) && f == "true");
        Created.Add(device);
        return device;
    }
}

internal sealed class FakeDevice(string pluginId, string deviceId, bool fail) : IDevicePlugin, IDeviceFaults, IAsyncDisposable
{
    public bool Disposed { get; private set; }
    public string Id => pluginId;
    public string DisplayName => "Fake device";
    public string DeviceId => deviceId;

    public string? DescribeFault(int code) => code == 9 ? "PV voltage too high" : null;

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInfo(deviceId, "Test", "Fake", null, null));

    public Task<IReadOnlyList<Reading>> ReadAsync(CancellationToken cancellationToken = default) =>
        fail
            ? throw new IOException("Fake device is unreachable.")
            : Task.FromResult<IReadOnlyList<Reading>>([new Reading(deviceId, Metrics.PvPower, 100, "W", DateTimeOffset.UtcNow)]);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

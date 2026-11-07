using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolPnl.Core.Devices;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Polling;

public sealed class PollingOptions
{
    /// <summary>Seconds between polls of each device.</summary>
    public int IntervalSeconds { get; set; } = 10;
}

/// <summary>
/// Polls every active device on a fixed interval and stores the readings. The device list is read from
/// <see cref="DeviceRegistry"/> on each tick, so devices added, removed or paused (plugin disabled) in the UI take
/// effect on the next poll.
/// </summary>
public sealed class PollingService(
    DeviceRegistry registry,
    IReadingStore store,
    IOptions<PollingOptions> options,
    ILogger<PollingService> logger) : BackgroundService
{
    private readonly Dictionary<int, int> _consecutiveFailures = new();
    private bool _loggedIdle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds));
        logger.LogInformation("Polling active devices every {Interval}.", interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            var devices = registry.ActiveDevices;
            if (devices.Count == 0)
            {
                if (!_loggedIdle) logger.LogInformation("No active devices to poll. Add one on the Devices page.");
                _loggedIdle = true;
            }
            else
            {
                _loggedIdle = false;
                foreach (var device in devices)
                    await PollAsync(device, stoppingToken);
            }

            // Forget failure counts for devices that are no longer active.
            foreach (var id in _consecutiveFailures.Keys.Except(devices.Select(d => d.ConfigId)).ToList())
                _consecutiveFailures.Remove(id);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(ActiveDevice device, CancellationToken ct)
    {
        try
        {
            var info = await device.Plugin.GetDeviceInfoAsync(ct);
            var readings = await device.Plugin.ReadAsync(ct);
            await store.SaveAsync(info, readings, ct);

            if (_consecutiveFailures.Remove(device.ConfigId, out var failures))
                logger.LogInformation("Device '{Device}' recovered after {Failures} failed poll(s).", device.Name, failures);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = _consecutiveFailures[device.ConfigId] = _consecutiveFailures.GetValueOrDefault(device.ConfigId) + 1;
            // Log the first failure in full, then only occasionally, so an offline device doesn't flood the log.
            if (failures == 1)
                logger.LogWarning(ex, "Polling device '{Device}' failed.", device.Name);
            else if (failures % 30 == 0)
                logger.LogWarning("Polling device '{Device}' has failed {Failures} times in a row: {Message}", device.Name, failures, ex.Message);
        }
    }
}

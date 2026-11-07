using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Polling;

public sealed class PollingOptions
{
    /// <summary>Seconds between polls of each device plugin.</summary>
    public int IntervalSeconds { get; set; } = 10;
}

/// <summary>Polls every registered device plugin on a fixed interval and stores the readings.</summary>
public sealed class PollingService(
    IEnumerable<IDevicePlugin> plugins,
    IReadingStore store,
    IOptions<PollingOptions> options,
    ILogger<PollingService> logger) : BackgroundService
{
    private readonly Dictionary<string, DeviceInfo> _deviceInfo = new();
    private readonly Dictionary<string, int> _consecutiveFailures = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var devicePlugins = plugins.ToList();
        if (devicePlugins.Count == 0)
        {
            logger.LogWarning("No device plugins are configured; nothing to poll.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds));
        logger.LogInformation("Polling {Count} device plugin(s) every {Interval}.", devicePlugins.Count, interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            foreach (var plugin in devicePlugins)
                await PollAsync(plugin, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(IDevicePlugin plugin, CancellationToken ct)
    {
        try
        {
            if (!_deviceInfo.TryGetValue(plugin.Id, out var device))
            {
                device = await plugin.GetDeviceInfoAsync(ct);
                _deviceInfo[plugin.Id] = device;
            }

            var readings = await plugin.ReadAsync(ct);
            await store.SaveAsync(device, readings, ct);

            if (_consecutiveFailures.Remove(plugin.Id, out var failures))
                logger.LogInformation("{Plugin} recovered after {Failures} failed poll(s).", plugin.Id, failures);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = _consecutiveFailures[plugin.Id] = _consecutiveFailures.GetValueOrDefault(plugin.Id) + 1;
            // Log the first failure in full, then only occasionally, so an offline device doesn't flood the log.
            if (failures == 1)
                logger.LogWarning(ex, "Polling {Plugin} failed.", plugin.Id);
            else if (failures % 30 == 0)
                logger.LogWarning("Polling {Plugin} has failed {Failures} times in a row: {Message}", plugin.Id, failures, ex.Message);
        }
    }
}

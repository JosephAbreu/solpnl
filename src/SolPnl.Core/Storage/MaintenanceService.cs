using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SolPnl.Core.Storage;

/// <summary>Runs <see cref="RollupMaintenance"/> at startup and then on a fixed interval.</summary>
public sealed class MaintenanceService(
    RollupMaintenance maintenance,
    IOptions<RetentionOptions> options,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, options.Value.MaintenanceIntervalSeconds)));
        do
        {
            try
            {
                await maintenance.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Storage maintenance failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SolPnl.Core.Data;
using SolPnl.Core.Polling;
using SolPnl.Core.Storage;

namespace SolPnl.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers storage (driver from "Db"), schema migrations, the reading store, rollups and retention
    /// (options from "Retention") and the polling service (options from "Polling").
    /// Device plugins are registered separately as <c>IDevicePlugin</c>.
    /// </summary>
    public static IServiceCollection AddSolPnlCore(this IServiceCollection services, IConfiguration configuration)
    {
        var dbSettings = DbSettings.FromConfiguration(configuration);
        services.AddSingleton(dbSettings);
        services.AddDbContextFactory<SolPnlDb>(dbSettings.Configure);
        services.AddSingleton<MigrationService>();
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<RetentionOptions>(configuration.GetSection("Retention"));
        services.AddSingleton<IReadingStore, EfReadingStore>();
        services.AddSingleton<RollupMaintenance>();
        services.AddHostedService<MaintenanceService>();

        services.Configure<PollingOptions>(configuration.GetSection("Polling"));
        services.AddHostedService<PollingService>();
        return services;
    }
}

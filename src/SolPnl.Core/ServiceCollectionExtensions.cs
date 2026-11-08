using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SolPnl.Core.Backup;
using SolPnl.Core.Data;
using SolPnl.Core.Devices;
using SolPnl.Core.Notifications;
using SolPnl.Core.Polling;
using SolPnl.Core.Site;
using SolPnl.Core.Storage;

namespace SolPnl.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers storage (driver from "Db"), schema migrations, the reading store, rollups and retention
    /// (options from "Retention") and the polling service (options from "Polling").
    /// Device plugin types are registered by the host as <c>IDevicePluginType</c>; devices are configured in the UI.
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

        // Device configuration and running device instances. Plugin types are registered by the host as IDevicePluginType.
        services.AddSingleton<DeviceRegistry>();

        // Location, weather, alerts, daily summaries and insights. Weather/alert providers are registered by the host.
        services.AddSingleton<SiteSettingsStore>();
        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<DaySummaryStore>();
        services.AddSingleton<SiteService>();
        services.Configure<SiteOptions>(configuration.GetSection("Site"));
        services.AddHostedService<SiteBackgroundService>();

        // Plugin-level settings and notifications. Notification plugins are registered by the host.
        services.AddSingleton<PluginSettingsStore>();
        services.AddSingleton<NotificationService>();
        services.AddHostedService<NotificationBackgroundService>();

        // Backups (needs a DataDirectory registered by the host).
        services.AddSingleton<BackupService>();
        services.AddHostedService<BackupBackgroundService>();

        services.Configure<PollingOptions>(configuration.GetSection("Polling"));
        services.AddHostedService<PollingService>();
        return services;
    }
}

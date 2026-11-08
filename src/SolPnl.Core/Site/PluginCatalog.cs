using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Data;
using SolPnl.Core.Devices;

namespace SolPnl.Core.Site;

/// <summary>
/// All installed plugins: device plugins (via <see cref="DeviceRegistry"/>), service plugins (weather, alerts) and
/// notification plugins. Every plugin can be enabled or disabled; no stored state means enabled. Plugins that
/// implement <see cref="IConfigurablePlugin"/> also have their own settings.
/// </summary>
public sealed class PluginCatalog(
    IDbContextFactory<SolPnlDb> dbFactory,
    DeviceRegistry registry,
    IEnumerable<IWeatherProvider> weatherProviders,
    IEnumerable<IWeatherAlertProvider> alertProviders,
    IEnumerable<INotificationPlugin> notificationPlugins)
{
    private readonly IReadOnlyList<IWeatherProvider> _weather = weatherProviders.ToList();
    private readonly IReadOnlyList<IWeatherAlertProvider> _alerts = alertProviders.ToList();
    private readonly IReadOnlyList<INotificationPlugin> _notifiers = notificationPlugins.ToList();

    /// <summary>Raised after a plugin is enabled or disabled.</summary>
    public event Action? Changed;

    private IEnumerable<IPlugin> AllPlugins =>
        registry.PluginTypes.Cast<IPlugin>().Concat(_weather).Concat(_alerts).Concat(_notifiers);

    public async Task<IReadOnlyList<PluginInfo>> GetPluginsAsync(CancellationToken ct = default)
    {
        var devicePlugins = (await registry.GetPluginsAsync(ct))
            .Select(p => p with { HasSettings = HasSettings(registry.PluginTypes.FirstOrDefault(t => t.Id == p.Id)) });
        var states = await StatesAsync(ct);

        PluginInfo Info(IPlugin p, string description, string kind) =>
            new(p.Id, p.DisplayName, description, kind, states.GetValueOrDefault(p.Id, true), 0, [], HasSettings(p));

        var services = _weather.Select(p => Info(p, p.Description, PluginKinds.Service))
            .Concat(_alerts.Select(p => Info(p, p.Description, PluginKinds.Service)))
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCulture);
        var notifiers = _notifiers.Select(p => Info(p, p.Description, PluginKinds.Notification))
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCulture);

        return devicePlugins.Concat(services).Concat(notifiers).ToList();
    }

    public async Task<PluginInfo> SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        if (registry.PluginTypes.Any(t => t.Id == pluginId))
        {
            await registry.SetPluginEnabledAsync(pluginId, enabled, ct);
            Changed?.Invoke();
            return (await GetPluginsAsync(ct)).Single(p => p.Id == pluginId);
        }

        if (!AllPlugins.Any(p => p.Id == pluginId))
            throw new KeyNotFoundException($"No plugin '{pluginId}' is installed.");

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var state = await db.PluginStates.SingleOrDefaultAsync(s => s.PluginId == pluginId, ct);
            if (state is null) db.PluginStates.Add(new PluginStateEntity { PluginId = pluginId, Enabled = enabled ? 1 : 0 });
            else state.Enabled = enabled ? 1 : 0;
            await db.SaveChangesAsync(ct);
        }

        Changed?.Invoke();
        return (await GetPluginsAsync(ct)).Single(p => p.Id == pluginId);
    }

    /// <summary>An installed plugin with its own settings, or null.</summary>
    public IConfigurablePlugin? FindConfigurable(string pluginId) =>
        AllPlugins.OfType<IConfigurablePlugin>().FirstOrDefault(p => p.Id == pluginId && p.PluginSettings.Count > 0);

    public async Task<bool> IsEnabledAsync(string pluginId, CancellationToken ct = default) =>
        (await StatesAsync(ct)).GetValueOrDefault(pluginId, true);

    /// <summary>The first enabled weather provider, if any.</summary>
    public async Task<IWeatherProvider?> WeatherProviderAsync(CancellationToken ct = default)
    {
        var states = await StatesAsync(ct);
        return _weather.FirstOrDefault(p => states.GetValueOrDefault(p.Id, true));
    }

    /// <summary>The first enabled provider that can search for places, if any.</summary>
    public async Task<ILocationSearch?> LocationSearchAsync(CancellationToken ct = default)
    {
        var states = await StatesAsync(ct);
        return _weather.OfType<ILocationSearch>().FirstOrDefault(p => states.GetValueOrDefault(p.Id, true));
    }

    public async Task<IReadOnlyList<IWeatherAlertProvider>> AlertProvidersAsync(CancellationToken ct = default)
    {
        var states = await StatesAsync(ct);
        return _alerts.Where(p => states.GetValueOrDefault(p.Id, true)).ToList();
    }

    public async Task<IReadOnlyList<INotificationPlugin>> NotificationPluginsAsync(CancellationToken ct = default)
    {
        var states = await StatesAsync(ct);
        return _notifiers.Where(p => states.GetValueOrDefault(p.Id, true)).ToList();
    }

    private static bool HasSettings(IPlugin? plugin) => plugin is IConfigurablePlugin { PluginSettings.Count: > 0 };

    private async Task<Dictionary<string, bool>> StatesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PluginStates.AsNoTracking().ToDictionaryAsync(s => s.PluginId, s => s.Enabled != 0, ct);
    }
}

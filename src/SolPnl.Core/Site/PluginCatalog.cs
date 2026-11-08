using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Data;
using SolPnl.Core.Devices;

namespace SolPnl.Core.Site;

/// <summary>
/// All installed plugins: device plugins (via <see cref="DeviceRegistry"/>) and service plugins (weather, alerts).
/// Every plugin can be enabled or disabled; no stored state means enabled.
/// </summary>
public sealed class PluginCatalog(
    IDbContextFactory<SolPnlDb> dbFactory,
    DeviceRegistry registry,
    IEnumerable<IWeatherProvider> weatherProviders,
    IEnumerable<IWeatherAlertProvider> alertProviders)
{
    private readonly IReadOnlyList<IWeatherProvider> _weather = weatherProviders.ToList();
    private readonly IReadOnlyList<IWeatherAlertProvider> _alerts = alertProviders.ToList();

    /// <summary>Raised after a plugin is enabled or disabled.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<PluginInfo>> GetPluginsAsync(CancellationToken ct = default)
    {
        var devicePlugins = await registry.GetPluginsAsync(ct);
        var states = await StatesAsync(ct);

        var services = _weather.Select(p => new PluginInfo(p.Id, p.DisplayName, p.Description, PluginKinds.Service,
                states.GetValueOrDefault(p.Id, true), 0, []))
            .Concat(_alerts.Select(p => new PluginInfo(p.Id, p.DisplayName, p.Description, PluginKinds.Service,
                states.GetValueOrDefault(p.Id, true), 0, [])));

        return devicePlugins.Concat(services.OrderBy(p => p.DisplayName, StringComparer.CurrentCulture)).ToList();
    }

    public async Task<PluginInfo> SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        if (registry.PluginTypes.Any(t => t.Id == pluginId))
        {
            var device = await registry.SetPluginEnabledAsync(pluginId, enabled, ct);
            Changed?.Invoke();
            return device;
        }

        if (!_weather.Any(p => p.Id == pluginId) && !_alerts.Any(p => p.Id == pluginId))
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

    private async Task<Dictionary<string, bool>> StatesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PluginStates.AsNoTracking().ToDictionaryAsync(s => s.PluginId, s => s.Enabled != 0, ct);
    }
}

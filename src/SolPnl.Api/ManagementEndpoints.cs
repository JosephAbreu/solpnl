using SolPnl.Contracts.Models;
using SolPnl.Core.Devices;
using SolPnl.Core.Notifications;
using SolPnl.Core.Site;
using SolPnl.Core.Storage;

namespace SolPnl.Api;

/// <summary>Device and plugin management. Note: no authentication yet, so anyone on the LAN can use these.</summary>
public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this RouteGroupBuilder api)
    {
        var plugins = api.MapGroup("/plugins");

        plugins.MapGet("", (PluginCatalog catalog, CancellationToken ct) => catalog.GetPluginsAsync(ct))
               .WithName("GetPlugins");

        plugins.MapPut("/{id}", async (string id, SetPluginEnabledRequest request, PluginCatalog catalog, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await catalog.SetEnabledAsync(id, request.Enabled, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ApiError(ex.Message));
            }
        }).WithName("SetPluginEnabled");

        // Plugin-level settings (cards on the Settings page). Secrets are never returned.
        plugins.MapGet("/{id}/settings", async (string id, PluginCatalog catalog, PluginSettingsStore store, CancellationToken ct) =>
            catalog.FindConfigurable(id) is { } plugin
                ? Results.Ok(await store.GetInfoAsync(plugin, ct))
                : Results.NotFound(new ApiError($"Plugin '{id}' has no settings.")))
            .WithName("GetPluginSettings");

        plugins.MapPut("/{id}/settings", async (string id, SavePluginSettingsRequest request, PluginCatalog catalog,
            PluginSettingsStore store, CancellationToken ct) =>
        {
            if (catalog.FindConfigurable(id) is not { } plugin) return Results.NotFound(new ApiError($"Plugin '{id}' has no settings."));
            try
            {
                return Results.Ok(await store.SaveAsync(plugin, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("SavePluginSettings");

        // Tries settings from the form (merged with saved secrets) without saving them.
        plugins.MapPost("/{id}/settings/test", async (string id, SavePluginSettingsRequest request, PluginCatalog catalog,
            PluginSettingsStore store, CancellationToken ct) =>
        {
            if (catalog.FindConfigurable(id) is not { } plugin) return Results.NotFound(new ApiError($"Plugin '{id}' has no settings."));
            if (plugin.TestLabel is null) return Results.BadRequest(new ApiError("This plugin has no test."));
            try
            {
                var merged = await store.MergeAsync(plugin, request, ct);
                foreach (var field in plugin.PluginSettings)
                    if (!merged.ContainsKey(field.Key) && field.Default is not null) merged[field.Key] = field.Default;
                return Results.Ok(await plugin.TestAsync(merged, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.Ok(new PluginActionResult(false, ex.Message));
            }
        }).WithName("TestPluginSettings");

        var notifications = api.MapGroup("/notifications");

        notifications.MapGet("/preferences", (NotificationService service, CancellationToken ct) => service.GetPreferencesAsync(ct))
            .WithName("GetNotificationPreferences");

        notifications.MapPut("/preferences", async (NotificationPreferences preferences, NotificationService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.SetPreferencesAsync(preferences, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("SetNotificationPreferences");

        notifications.MapPost("/test", (NotificationService service, CancellationToken ct) => service.SendTestAsync(ct))
            .WithName("SendTestNotification");

        // Open conditions plus alert history, newest first. Page with ?before=<id of the last entry shown>.
        api.MapGet("/alerts", (string? category, int? limit, long? before, NotificationService service, CancellationToken ct) =>
                service.GetAlertsAsync(category, limit ?? 50, before, ct))
            .WithName("GetAlerts");

        var config = api.MapGroup("/config/devices");

        config.MapGet("", (DeviceRegistry registry, CancellationToken ct) => registry.GetDevicesAsync(ct))
              .WithName("GetConfiguredDevices");

        config.MapPost("/test", (AddDeviceRequest request, DeviceRegistry registry, CancellationToken ct) =>
                  registry.TestAsync(request.PluginId ?? "", request.Settings ?? [], ct))
              .WithName("TestDevice");

        config.MapPost("", async (AddDeviceRequest request, DeviceRegistry registry, CancellationToken ct) =>
        {
            try
            {
                var device = await registry.AddAsync(request, ct);
                return Results.Created($"/api/v1/config/devices/{device.Id}", device);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
            catch (DeviceConfigurationException ex)
            {
                return Results.Conflict(new ApiError(ex.Message));
            }
        }).WithName("AddDevice");

        config.MapDelete("/{id:int}", async (int id, bool? deleteHistory, DeviceRegistry registry, CancellationToken ct) =>
                await registry.RemoveAsync(id, deleteHistory == true, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new ApiError($"No configured device with id {id}.")))
              .WithName("RemoveDevice");

        // History of a device that is no longer configured (configured devices are removed via /config/devices).
        api.MapDelete("/devices/{deviceId}", async (string deviceId, DeviceRegistry registry, IReadingStore store, CancellationToken ct) =>
        {
            if ((await registry.GetDevicesAsync(ct)).Any(d => d.DeviceId == deviceId))
                return Results.Conflict(new ApiError("This device is still configured. Remove it from the device page instead."));
            return await store.DeleteDeviceAsync(deviceId, ct)
                ? Results.NoContent()
                : Results.NotFound(new ApiError($"No stored history for device '{deviceId}'."));
        }).WithName("DeleteDeviceHistory");
    }
}

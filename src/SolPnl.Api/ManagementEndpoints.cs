using SolPnl.Contracts.Models;
using SolPnl.Core.Devices;
using SolPnl.Core.Storage;

namespace SolPnl.Api;

/// <summary>Device and plugin management. Note: no authentication yet, so anyone on the LAN can use these.</summary>
public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this RouteGroupBuilder api)
    {
        var plugins = api.MapGroup("/plugins");

        plugins.MapGet("", (DeviceRegistry registry, CancellationToken ct) => registry.GetPluginsAsync(ct))
               .WithName("GetPlugins");

        plugins.MapPut("/{id}", async (string id, SetPluginEnabledRequest request, DeviceRegistry registry, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await registry.SetPluginEnabledAsync(id, request.Enabled, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ApiError(ex.Message));
            }
        }).WithName("SetPluginEnabled");

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

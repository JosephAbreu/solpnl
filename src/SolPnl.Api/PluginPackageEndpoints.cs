using Microsoft.AspNetCore.Http.Features;
using SolPnl.Contracts.Models;
using SolPnl.Core.Plugins;
using SolPnl.Core.Site;

namespace SolPnl.Api;

/// <summary>
/// Adding and removing plugin packages. Every change restarts SolPnl, which loads the plugins anew. Uploading runs code
/// on this machine, and the API has no login yet, so it is off unless <c>Plugins:AllowUpload</c> is true in
/// appsettings.Local.json.
/// </summary>
public static class PluginPackageEndpoints
{
    public const string AllowUploadKey = "Plugins:AllowUpload";

    public static void MapPluginPackageEndpoints(this RouteGroupBuilder api)
    {
        var packages = api.MapGroup("/plugin-packages");

        packages.MapGet("", (PluginPackages packages, PluginCatalog catalog, IConfiguration config) =>
                new PluginPackageList(UploadAllowed(config), packages.List(catalog.AllPlugins)))
            .WithName("GetPluginPackages");

        // The request body is the package zip.
        packages.MapPost("", async (HttpContext context, PluginPackages packages, IConfiguration config, RestartSignal restart,
            IHostApplicationLifetime lifetime, ILogger<PluginPackages> logger, CancellationToken ct) =>
        {
            if (!UploadAllowed(config))
                return Results.Json(new ApiError(
                    "Adding plugins from the browser is turned off. To turn it on, add \"Plugins\": { \"AllowUpload\": true } " +
                    "to appsettings.Local.json in SolPnl's data directory and restart SolPnl. Or unzip the package into its own " +
                    $"folder in {packages.AddedDirectory} and restart SolPnl."), statusCode: StatusCodes.Status403Forbidden);

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = PluginPackages.MaxPackageBytes + 1;
            try
            {
                var manifest = await packages.InstallAsync(context.Request.Body, ct);
                logger.LogWarning("Plugin package {Id} ({Name}) was added. Restarting to load it.", manifest.Id, manifest.Name);
                return Restarting(restart, lifetime, $"Added {manifest.Name}. SolPnl is restarting to load it; this page will reconnect when it's back.");
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("AddPluginPackage");

        packages.MapDelete("/{id}", (string id, PluginPackages packages, RestartSignal restart, IHostApplicationLifetime lifetime,
            ILogger<PluginPackages> logger) =>
        {
            try
            {
                packages.Remove(id);
                logger.LogWarning("Plugin package {Id} was removed. Restarting.", id);
                return Restarting(restart, lifetime, "Removed. SolPnl is restarting; this page will reconnect when it's back.");
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ApiError(ex.Message));
            }
        }).WithName("RemovePluginPackage");

        packages.MapPost("/{id}/restore", (string id, PluginPackages packages, RestartSignal restart, IHostApplicationLifetime lifetime,
            ILogger<PluginPackages> logger) =>
        {
            try
            {
                packages.Restore(id);
                logger.LogInformation("Built-in plugin package {Id} was restored. Restarting to load it.", id);
                return Restarting(restart, lifetime, "Restored. SolPnl is restarting to load it; this page will reconnect when it's back.");
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ApiError(ex.Message));
            }
        }).WithName("RestorePluginPackage");
    }

    private static bool UploadAllowed(IConfiguration config) => config.GetValue<bool>(AllowUploadKey);

    private static IResult Restarting(RestartSignal restart, IHostApplicationLifetime lifetime, string message)
    {
        restart.Request(lifetime);
        return Results.Ok(new PluginActionResult(true, message));
    }
}

using Microsoft.AspNetCore.Http.Features;
using SolPnl.Contracts.Models;
using SolPnl.Core.Backup;

namespace SolPnl.Api;

/// <summary>Backups: schedule, run now, download, delete, restore. No authentication yet, like the rest of the API.</summary>
public static class BackupEndpoints
{
    /// <summary>Uploads for restore can be larger than Kestrel's 30 MB default.</summary>
    private const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    public static void MapBackupEndpoints(this RouteGroupBuilder api)
    {
        var backups = api.MapGroup("/backups");

        backups.MapGet("", (BackupService service, CancellationToken ct) => service.GetStatusAsync(ct))
               .WithName("GetBackups");

        backups.MapPut("/settings", async (BackupSettings settings, BackupService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.SaveSettingsAsync(settings, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("SaveBackupSettings");

        // Starts a backup and returns; it can take a minute or two on a Pi. The Backups page polls GET /backups.
        backups.MapPost("", (BackupService service, IHostApplicationLifetime lifetime) =>
            service.TryStart(BackupKinds.Manual, lifetime.ApplicationStopping)
                ? Results.Accepted("/api/v1/backups", new PluginActionResult(true, "Backup started."))
                : Results.Conflict(new ApiError("A backup is already running. Try again when it's finished.")))
               .WithName("RunBackup");

        backups.MapGet("/{name}", async (string name, BackupService service, CancellationToken ct) =>
            await service.OpenAsync(name, ct) is { } file
                ? Results.File(file, "application/zip", name)
                : Results.NotFound(new ApiError("There's no backup with that name.")))
               .WithName("DownloadBackup");

        backups.MapDelete("/{name}", async (string name, BackupService service, CancellationToken ct) =>
            await service.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound(new ApiError("There's no backup with that name.")))
               .WithName("DeleteBackup");

        backups.MapPost("/{name}/restore", async (string name, BackupService service, RestartSignal restart,
            IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            try
            {
                if (await service.StageRestoreAsync(name, ct) is not { } manifest)
                    return Results.NotFound(new ApiError("There's no backup with that name."));
                return Restarting(manifest, restart, lifetime);
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("RestoreBackup");

        // Restore from an uploaded file (the request body is the backup zip).
        backups.MapPost("/restore", async (HttpContext context, BackupService service, RestartSignal restart,
            IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxUploadBytes;
            try
            {
                return Restarting(await service.StageRestoreAsync(context.Request.Body, ct), restart, lifetime);
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).WithName("RestoreUploadedBackup");
    }

    private static IResult Restarting(BackupManifest manifest, RestartSignal restart, IHostApplicationLifetime lifetime)
    {
        restart.Request(lifetime);
        return Results.Ok(new PluginActionResult(true,
            $"Restoring the backup from {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC. SolPnl is restarting; this page will " +
            "reconnect when it's back. That takes a few minutes, longer with a lot of history."));
    }
}

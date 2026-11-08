using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;
using SolPnl.Core.Devices;
using SolPnl.Core.Setup;
using SolPnl.Core.Storage;

namespace SolPnl.Api;

public static class Endpoints
{
    public static void MapHealthAndSetupStatus(this RouteGroupBuilder api, bool setupRequired)
    {
        api.MapGet("/health", () => Results.Ok(new { status = "ok", setupRequired, time = DateTimeOffset.UtcNow }))
           .WithName("GetHealth");

        api.MapGet("/setup/status", () => new SetupStatus(setupRequired))
           .WithName("GetSetupStatus");

        api.MapGet("/about", async (IServiceProvider services, CancellationToken ct) =>
        {
            // Storage services exist only once setup is done.
            var database = services.GetService<DbSettings>() is { } settings
                ? await DatabaseDescription.DescribeAsync(services.GetRequiredService<IDbContextFactory<SolPnlDb>>(), settings, ct)
                : null;
            return new AboutInfo(ServerVersion.Value, ServerCommit.Value, RuntimeInformation.FrameworkDescription,
                $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})",
                database, ProcessStart.Value);
        }).WithName("GetAbout");
    }

    private static readonly Lazy<string> ServerVersion = new(() =>
        typeof(Endpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    private static readonly Lazy<string?> ServerCommit = new(() =>
        typeof(Endpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "Commit")?.Value);

    private static readonly Lazy<DateTimeOffset> ProcessStart = new(() =>
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    });

    /// <summary>Setup wizard endpoints; only mapped while no storage is configured.</summary>
    public static void MapSetupEndpoints(this RouteGroupBuilder api)
    {
        var setup = api.MapGroup("/setup");

        setup.MapGet("/defaults", (SetupPaths paths) => new SetupDefaults(paths.DefaultSqlitePath))
             .WithName("GetSetupDefaults");

        setup.MapPost("/storage/test", async (StorageSetupRequest request, SetupState state, CancellationToken ct) =>
        {
            if (CheckCode(request, state) is { } denied) return denied;
            if (TryBuild(request, out var settings) is { } invalid) return invalid;
            return Results.Ok(await StorageSetupService.TestAsync(settings!, ct));
        }).WithName("TestStorage");

        setup.MapPost("/storage", async (StorageSetupRequest request, SetupState state, StorageSetupService service,
            RestartSignal restart, IHostApplicationLifetime lifetime, ILogger<StorageSetupService> logger, CancellationToken ct) =>
        {
            if (CheckCode(request, state) is { } denied) return denied;
            if (TryBuild(request, out var settings) is { } invalid) return invalid;

            var result = await service.ApplyAsync(settings!, ct);
            if (!result.Ok) return Results.Ok(result);

            logger.LogInformation("Storage configured ({Driver}). Restarting into normal mode.", settings!.DriverName);
            restart.Request(lifetime);
            return Results.Ok(result with { Message = "Storage is set up. SolPnl is restarting..." });
        }).WithName("SaveStorage");
    }

    public static void MapReadingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/devices", (IReadingStore store, CancellationToken ct) => store.GetDevicesAsync(ct))
           .WithName("GetDevices");

        // Faults the device reports right now (empty if none, or if it doesn't report faults), described by its plugin.
        api.MapGet("/devices/{deviceId}/faults", async (string deviceId, IReadingStore store, DeviceRegistry registry,
               TimeProvider time, CancellationToken ct) =>
           {
               var latest = await store.GetLatestAsync(deviceId, ct);
               var plugin = registry.ActiveDevices.FirstOrDefault(d => d.DeviceKey == deviceId)?.Plugin;
               return (DeviceFaults.Active(latest, time.GetUtcNow()) ?? [])
                   .Select(code => new DeviceFault(code, DeviceFaults.Describe(plugin, code)))
                   .ToList();
           })
           .WithName("GetDeviceFaults");

        api.MapGet("/readings/latest", (string? device, IReadingStore store, CancellationToken ct) =>
               store.GetLatestAsync(device, ct))
           .WithName("GetLatestReadings");

        // resolution: raw | minute | hour; omitted = chosen from the range (raw up to 2 h, minute up to 3 days, else hour).
        api.MapGet("/readings/history", async (string device, string metric, DateTimeOffset? from, DateTimeOffset? to,
               string? resolution, IReadingStore store, TimeProvider time, CancellationToken ct) =>
           {
               HistoryResolution? parsed = null;
               if (!string.IsNullOrEmpty(resolution) && !resolution.Equals("auto", StringComparison.OrdinalIgnoreCase))
               {
                   if (!Enum.TryParse<HistoryResolution>(resolution, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                       return Results.BadRequest(new { error = "'resolution' must be raw, minute, hour or auto." });
                   parsed = value;
               }

               var end = to ?? time.GetUtcNow();
               var start = from ?? end.AddHours(-24);
               if (start > end)
                   return Results.BadRequest(new { error = "'from' must be before 'to'." });
               return Results.Ok(await store.GetHistoryAsync(device, metric, start, end, parsed, ct));
           })
           .WithName("GetReadingHistory");
    }

    private static IResult? CheckCode(StorageSetupRequest request, SetupState state)
    {
        if (state.IsLockedOut)
            return Results.Json(new SetupResult(false, "Too many wrong setup codes. Restart SolPnl to get a new code."),
                statusCode: StatusCodes.Status429TooManyRequests);
        return state.Verify(request.Code)
            ? null
            : Results.Json(new SetupResult(false, "The setup code is wrong. It's shown in SolPnl's log."),
                statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult? TryBuild(StorageSetupRequest request, out DbSettings? settings)
    {
        try
        {
            settings = StorageSetupService.BuildSettings(request);
            return null;
        }
        catch (ArgumentException ex)
        {
            settings = null;
            return Results.BadRequest(new SetupResult(false, ex.Message));
        }
    }
}

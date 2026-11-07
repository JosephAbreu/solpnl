using System.Text.Json;
using System.Text.Json.Serialization;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core;
using SolPnl.Core.Data;
using SolPnl.Core.Storage;
using SolPnl.Plugin.SolarmanSrne;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific settings (device addresses, connection strings) live in an untracked file.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSolPnlCore(builder.Configuration);

// Device plugins. Registered directly for now; dynamic plugin loading comes later.
var srne = builder.Configuration.GetSection("Plugins:SolarmanSrne");
if (srne.Exists())
{
    var srneOptions = srne.Get<SolarmanSrneOptions>()
        ?? throw new InvalidOperationException("Plugins:SolarmanSrne is invalid.");
    builder.Services.AddSingleton<IDevicePlugin>(_ => new SolarmanSrnePlugin(srneOptions));
}

var app = builder.Build();

await app.Services.GetRequiredService<MigrationService>().MigrateAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
}

// Serve the Blazor WebAssembly PWA (SolPnl.Web) from this same process: one service on the Pi.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api/v1");

api.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }))
   .WithName("GetHealth");

api.MapGet("/devices", (IReadingStore store, CancellationToken ct) => store.GetDevicesAsync(ct))
   .WithName("GetDevices");

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

app.MapFallbackToFile("index.html");

app.Run();

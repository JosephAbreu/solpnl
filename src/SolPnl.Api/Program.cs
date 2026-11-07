using System.Text.Json;
using System.Text.Json.Serialization;
using SolPnl.Api;
using SolPnl.Contracts.Plugins;
using SolPnl.Core;
using SolPnl.Core.Data;
using SolPnl.Core.Setup;
using SolPnl.Plugin.SolarmanSrne;

// SolPnl runs in one of two modes:
//  - Setup mode: no storage configured yet. Serves the web app and the setup wizard only (gated by a one-time code
//    printed to the log). Saving the wizard writes the storage choice and restarts into normal mode.
//  - Normal mode: storage, polling, maintenance and the full API.
// The host is rebuilt in-process on restart, so this works the same under systemd and `dotnet run`.
var restart = new RestartSignal();
do
{
    restart.Reset();
    var app = BuildApp(args, restart);
    if (app.Services.GetService<MigrationService>() is { } migrations)
        await migrations.MigrateAsync();
    await app.RunAsync();
}
while (restart.Requested);

static WebApplication BuildApp(string[] args, RestartSignal restart)
{
    var builder = WebApplication.CreateBuilder(args);

    // Machine-specific settings (storage, device addresses) live in an untracked file in the data directory:
    // SOLPNL_DATA_DIR if set (e.g. /var/lib/solpnl on the Pi), otherwise the content root.
    var dataDirectory = Environment.GetEnvironmentVariable("SOLPNL_DATA_DIR") is { Length: > 0 } dir
        ? Path.GetFullPath(dir)
        : builder.Environment.ContentRootPath;
    var settingsFile = new LocalSettingsFile(Path.Combine(dataDirectory, LocalSettingsFile.FileName));
    builder.Configuration.AddJsonFile(settingsFile.Path, optional: true, reloadOnChange: false);

    builder.Services.AddOpenApi();
    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
    builder.Services.AddSingleton(restart);
    builder.Services.AddSingleton(settingsFile);
    builder.Services.AddSingleton(new SetupPaths(dataDirectory));

    var setupRequired = !DbSettings.IsConfigured(builder.Configuration);
    if (setupRequired)
    {
        builder.Services.AddSingleton(new SetupState());
        builder.Services.AddSingleton(sp => new StorageSetupService(settingsFile, sp.GetRequiredService<ILoggerFactory>()));
    }
    else
    {
        builder.Services.AddSolPnlCore(builder.Configuration);

        // Device plugins. Registered directly for now; dynamic plugin loading comes later.
        var srne = builder.Configuration.GetSection("Plugins:SolarmanSrne");
        if (srne.Exists())
        {
            var srneOptions = srne.Get<SolarmanSrneOptions>()
                ?? throw new InvalidOperationException("Plugins:SolarmanSrne is invalid.");
            builder.Services.AddSingleton<IDevicePlugin>(_ => new SolarmanSrnePlugin(srneOptions));
        }
    }

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseWebAssemblyDebugging();
    }

    // Serve the Blazor WebAssembly PWA (SolPnl.Web) from this same process: one service on the Pi.
    // MapStaticAssets serves the WebAssembly client (_framework) and the fingerprinted URLs .NET 10 generates for
    // app and library assets (e.g. _content/SolPnl.Ui/solpnl.<hash>.js), with compression and caching headers.
    // (UseBlazorFrameworkFiles must not be added: its /_framework branch bypasses these endpoints.)
    app.MapStaticAssets();

    var api = app.MapGroup("/api/v1");
    api.MapHealthAndSetupStatus(setupRequired);

    if (setupRequired)
    {
        api.MapSetupEndpoints();
        app.Lifetime.ApplicationStarted.Register(() => LogSetupCode(app));
    }
    else
    {
        api.MapReadingEndpoints();
    }

    // Unknown API routes get a JSON error instead of the web app's index.html.
    app.MapFallback("/api/{**path}", () => setupRequired
        ? Results.Json(new { error = "SolPnl needs to be set up first. Open /setup." }, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.NotFound(new { error = "Unknown API endpoint." }));
    app.MapFallbackToFile("index.html");

    return app;
}

static void LogSetupCode(WebApplication app)
{
    var code = app.Services.GetRequiredService<SetupState>().Code;
    var urls = string.Join(", ", app.Urls.Select(u => u.TrimEnd('/') + "/setup"));
    app.Logger.LogWarning(
        "SolPnl needs to be set up. Open {Urls} in a browser and enter setup code {Code}. " +
        "(Use this machine's network address instead of localhost/0.0.0.0 when browsing from another device.)",
        urls, code);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

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

app.MapFallbackToFile("index.html");

app.Run();

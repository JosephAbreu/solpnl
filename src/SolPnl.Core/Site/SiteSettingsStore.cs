using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;

namespace SolPnl.Core.Site;

/// <summary>Site-wide settings (location, display units), stored in the app_setting table.</summary>
public sealed class SiteSettingsStore(IDbContextFactory<SolPnlDb> dbFactory)
{
    private const string LocationKey = "site.location";
    private const string TemperatureUnitKey = "units.temperature";

    /// <summary>Raised after the location changes, so weather can refresh right away.</summary>
    public event Action? LocationChanged;

    public async Task<SiteLocation?> GetLocationAsync(CancellationToken ct = default)
    {
        var json = await GetAsync(LocationKey, ct);
        return json is null ? null : JsonSerializer.Deserialize<SiteLocation>(json);
    }

    /// <summary>Validates and saves the location. Throws ArgumentException with a user-facing message.</summary>
    public async Task<SiteLocation> SetLocationAsync(SiteLocation location, CancellationToken ct = default)
    {
        var validated = Validate(location);
        await SetAsync(LocationKey, JsonSerializer.Serialize(validated), ct);
        LocationChanged?.Invoke();
        return validated;
    }

    public async Task<string> GetTemperatureUnitAsync(CancellationToken ct = default) =>
        await GetAsync(TemperatureUnitKey, ct) ?? TemperatureUnits.Fahrenheit;

    public async Task SetTemperatureUnitAsync(string unit, CancellationToken ct = default)
    {
        if (unit is not (TemperatureUnits.Celsius or TemperatureUnits.Fahrenheit))
            throw new ArgumentException("Temperature unit must be \"C\" or \"F\".");
        await SetAsync(TemperatureUnitKey, unit, ct);
    }

    public static SiteLocation Validate(SiteLocation location)
    {
        if (double.IsNaN(location.Latitude) || location.Latitude is < -90 or > 90)
            throw new ArgumentException("Latitude must be between -90 and 90.");
        if (double.IsNaN(location.Longitude) || location.Longitude is < -180 or > 180)
            throw new ArgumentException("Longitude must be between -180 and 180.");
        if (location.ElevationMeters is { } e && (double.IsNaN(e) || e is < -500 or > 9000))
            throw new ArgumentException("Elevation must be between -500 and 9,000 meters.");

        var zoneId = string.IsNullOrWhiteSpace(location.TimeZoneId) ? TimeZoneInfo.Local.Id : location.TimeZoneId.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
            throw new ArgumentException($"Unknown time zone '{zoneId}'. Use an IANA name such as America/Chicago.");
        // Store the IANA name where possible, so the setting means the same on Windows and Linux.
        if (!zone.HasIanaId && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)) zoneId = iana;

        var name = string.IsNullOrWhiteSpace(location.Name) ? null : location.Name.Trim();
        if (name is { Length: > 100 }) throw new ArgumentException("Name must be at most 100 characters.");

        return new SiteLocation(name, Math.Round(location.Latitude, 5), Math.Round(location.Longitude, 5),
            location.ElevationMeters is { } m ? Math.Round(m, 1) : null, zoneId);
    }

    public static TimeZoneInfo Zone(SiteLocation location) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(location.TimeZoneId, out var zone) ? zone : TimeZoneInfo.Local;

    private async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
    }

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSettingEntity { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }
}

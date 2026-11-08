using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Data;

namespace SolPnl.Core.Site;

/// <summary>
/// Stores plugin-level settings and validates them against the plugin's <see cref="SettingField"/> list.
/// Secret values are stored like the database connection string (only readable by SolPnl's service account on the
/// Pi) and are never returned by the API.
/// </summary>
public sealed class PluginSettingsStore(IDbContextFactory<SolPnlDb> dbFactory)
{
    public async Task<Dictionary<string, string?>> GetValuesAsync(string pluginId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PluginSettings.AsNoTracking().Where(s => s.PluginId == pluginId)
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.Ordinal, ct);
    }

    /// <summary>Settings as the UI may see them: secrets blanked, with a list of which secrets are set.</summary>
    public async Task<PluginSettingsInfo> GetInfoAsync(IConfigurablePlugin plugin, CancellationToken ct = default)
    {
        var values = await GetValuesAsync(plugin.Id, ct);
        return ToInfo(plugin, values);
    }

    /// <summary>Saved values merged with a request (see <see cref="SavePluginSettingsRequest"/>), then validated.</summary>
    public async Task<Dictionary<string, string?>> MergeAsync(IConfigurablePlugin plugin, SavePluginSettingsRequest request, CancellationToken ct = default)
    {
        var saved = await GetValuesAsync(plugin.Id, ct);
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in plugin.PluginSettings)
        {
            request.Values.TryGetValue(field.Key, out var incoming);
            string? value;
            if (field.Kind == SettingKind.Secret)
                value = incoming is null ? saved.GetValueOrDefault(field.Key) : incoming.Length == 0 ? null : incoming;
            else
                value = string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim();
            if (value is not null) merged[field.Key] = value;
        }
        Validate(plugin.PluginSettings, merged);
        return merged;
    }

    public async Task<PluginSettingsInfo> SaveAsync(IConfigurablePlugin plugin, SavePluginSettingsRequest request, CancellationToken ct = default)
    {
        var merged = await MergeAsync(plugin, request, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.PluginSettings.Where(s => s.PluginId == plugin.Id).ToListAsync(ct);
        db.PluginSettings.RemoveRange(existing.Where(e => !merged.ContainsKey(e.Key)));
        foreach (var (key, value) in merged)
        {
            var row = existing.FirstOrDefault(e => e.Key == key);
            if (row is null) db.PluginSettings.Add(new PluginSettingEntity { PluginId = plugin.Id, Key = key, Value = value });
            else row.Value = value;
        }
        await db.SaveChangesAsync(ct);
        return ToInfo(plugin, merged);
    }

    /// <summary>Saved values with defaults applied for missing optional fields; what plugins receive.</summary>
    public async Task<Dictionary<string, string?>> GetEffectiveAsync(IConfigurablePlugin plugin, CancellationToken ct = default)
    {
        var values = await GetValuesAsync(plugin.Id, ct);
        foreach (var field in plugin.PluginSettings)
            if (!values.ContainsKey(field.Key) && field.Default is not null) values[field.Key] = field.Default;
        return values;
    }

    /// <summary>True when every required field has a value.</summary>
    public static bool IsConfigured(IReadOnlyList<SettingField> fields, IReadOnlyDictionary<string, string?> values) =>
        fields.Where(f => f.Required).All(f => !string.IsNullOrEmpty(values.GetValueOrDefault(f.Key)));

    /// <summary>Checks numbers and choices. Required fields may be missing (the plugin then counts as not configured).</summary>
    public static void Validate(IReadOnlyList<SettingField> fields, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var field in fields)
        {
            if (values.GetValueOrDefault(field.Key) is not { } value) continue;
            switch (field.Kind)
            {
                case SettingKind.Number when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                    throw new ArgumentException($"{field.Label} must be a number.");
                case SettingKind.Choice when field.Options is { } options && !options.Contains(value):
                    throw new ArgumentException($"{field.Label} must be one of: {string.Join(", ", options)}.");
            }
        }
    }

    private static PluginSettingsInfo ToInfo(IConfigurablePlugin plugin, IReadOnlyDictionary<string, string?> values)
    {
        var visible = new Dictionary<string, string?>(StringComparer.Ordinal);
        var secretsSet = new List<string>();
        foreach (var field in plugin.PluginSettings)
        {
            var value = values.GetValueOrDefault(field.Key);
            if (field.Kind == SettingKind.Secret)
            {
                if (!string.IsNullOrEmpty(value)) secretsSet.Add(field.Key);
                visible[field.Key] = null;
            }
            else
            {
                visible[field.Key] = value;
            }
        }
        return new PluginSettingsInfo(plugin.Id, plugin.DisplayName, plugin.PluginSettings, visible, secretsSet,
            IsConfigured(plugin.PluginSettings, values), plugin.TestLabel);
    }
}

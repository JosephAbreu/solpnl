using System.Text.Json;
using System.Text.RegularExpressions;

namespace SolPnl.Core.Plugins;

/// <summary>
/// <c>plugin.json</c> at the root of a plugin package: which assembly to load, and how to name the package.
/// </summary>
/// <param name="Id">Lower case letters, digits, '.' and '-' (e.g. "solpnl.openmeteo"); one installed package per id.</param>
/// <param name="Assembly">The plugin's main assembly file in the package (e.g. "SolPnl.Plugin.OpenMeteo.dll").</param>
/// <param name="DevelopmentOnly">Loaded only in the Development environment (e.g. the simulated inverter).</param>
public sealed partial record PluginManifest(string Id, string Name, string Assembly, string? Description = null,
    bool DevelopmentOnly = false)
{
    public const string FileName = "plugin.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Reads and checks the manifest in <paramref name="directory"/>.</summary>
    /// <exception cref="InvalidDataException">Missing, unreadable or invalid manifest (the message says why).</exception>
    public static PluginManifest Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) throw new InvalidDataException($"The package has no {FileName} at its top level.");

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} isn't valid JSON: {ex.Message}");
        }

        if (manifest is null) throw new InvalidDataException($"{FileName} is empty.");
        if (!IsValidId(manifest.Id))
            throw new InvalidDataException($"{FileName} needs an \"id\" of lower case letters, digits, '.' and '-' (e.g. \"acme.inverter\").");
        if (string.IsNullOrWhiteSpace(manifest.Name)) throw new InvalidDataException($"{FileName} needs a \"name\".");
        if (string.IsNullOrWhiteSpace(manifest.Assembly) || Path.GetFileName(manifest.Assembly) != manifest.Assembly ||
            !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{FileName} needs an \"assembly\": the plugin's .dll file name in the package.");
        if (!File.Exists(Path.Combine(directory, manifest.Assembly)))
            throw new InvalidDataException($"The package doesn't contain {manifest.Assembly}, named in {FileName}.");
        return manifest;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolPnl.Core.Setup;

/// <summary>
/// Reads and updates appsettings.Local.json, the machine-specific settings file (device addresses, storage).
/// Updates merge one section at a time so other settings (e.g. Plugins) are kept.
/// </summary>
public sealed class LocalSettingsFile(string path)
{
    public const string FileName = "appsettings.Local.json";

    public string Path { get; } = path;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Replaces one top-level section and saves atomically (write to a temp file, then move).</summary>
    public async Task SetSectionAsync(string section, JsonObject value, CancellationToken cancellationToken = default)
    {
        JsonObject root;
        if (File.Exists(Path))
        {
            await using var read = File.OpenRead(Path);
            root = (await JsonNode.ParseAsync(read, documentOptions: ReadOptions, cancellationToken: cancellationToken)) as JsonObject
                   ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        root[section] = value;

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        var temp = System.IO.Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, root.ToJsonString(WriteOptions) + Environment.NewLine, cancellationToken);

        // May contain a database password: owner-only on Linux/macOS.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(temp, Path, overwrite: true);
    }
}

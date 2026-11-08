using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Core.Plugins;

/// <summary>
/// The plugin packages SolPnl loads at startup. Built-in packages ship in the release's <c>plugins</c> folder; packages
/// added on this machine live in the data directory's <c>plugins</c> folder and take precedence over a built-in one with
/// the same id. Installing, removing and restoring change files only; they take effect when SolPnl restarts.
/// </summary>
public sealed class PluginPackages : IDisposable
{
    /// <summary>The plugin kinds a package can provide; SolPnl creates each public class implementing one.</summary>
    public static readonly IReadOnlyList<Type> PluginInterfaces =
        [typeof(IDevicePluginType), typeof(IWeatherProvider), typeof(IWeatherAlertProvider), typeof(INotificationPlugin)];

    public const long MaxPackageBytes = 64L * 1024 * 1024;

    private const string RemovedFile = "removed-built-in.json";
    private const string SecretKeysFile = "secret-keys.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly List<Package> _packages = [];
    private readonly List<PluginLoadContext> _contexts = [];
    private readonly Dictionary<Assembly, string> _byAssembly = [];
    private readonly Lock _gate = new();

    private PluginPackages(string builtInDirectory, string addedDirectory)
    {
        BuiltInDirectory = builtInDirectory;
        AddedDirectory = addedDirectory;
    }

    public string BuiltInDirectory { get; }
    public string AddedDirectory { get; }

    private sealed class Package
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Directory { get; init; }
        public required string Source { get; init; }
        public string? Description { get; init; }
        public PluginManifest? Manifest { get; init; }
        public string? Version { get; set; }
        public string State { get; set; } = PluginPackageStates.Loaded;
        public string? Error { get; set; }
        public List<Type> Types { get; } = [];
    }

    /// <summary>Finds and loads every package that isn't removed. A package that fails to load is listed with its error.</summary>
    public static PluginPackages Load(string builtInDirectory, string addedDirectory, bool development)
    {
        var packages = new PluginPackages(builtInDirectory, addedDirectory);
        var removed = packages.ReadRemoved();
        var added = Discover(addedDirectory, PluginPackageSources.Added);
        var addedIds = added.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        var all = Discover(builtInDirectory, PluginPackageSources.BuiltIn).Where(p => !addedIds.Contains(p.Id)).Concat(added);
        foreach (var package in all)
        {
            if (package.Manifest is { DevelopmentOnly: true } && !development) continue;
            packages._packages.Add(package);
            if (package.State == PluginPackageStates.Failed) continue;
            if (package.Source == PluginPackageSources.BuiltIn && removed.Contains(package.Id))
                package.State = PluginPackageStates.Removed;
            else
                packages.LoadPackage(package);
        }
        return packages;
    }

    private static List<Package> Discover(string directory, string source)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        var found = new List<Package>();
        foreach (var dir in System.IO.Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith('.')) continue; // staging folder of an install in progress
            try
            {
                var manifest = PluginManifest.Read(dir);
                if (found.Any(p => p.Id == manifest.Id))
                {
                    found.Add(new Package
                    {
                        Id = folder, Name = manifest.Name, Directory = dir, Source = source, State = PluginPackageStates.Failed,
                        Error = $"Another package in the plugins folder has the same id ({manifest.Id}).",
                    });
                    continue;
                }
                found.Add(new Package
                {
                    Id = manifest.Id, Name = manifest.Name, Description = manifest.Description, Directory = dir, Source = source,
                    Manifest = manifest,
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                found.Add(new Package
                {
                    Id = folder, Name = folder, Directory = dir, Source = source, State = PluginPackageStates.Failed, Error = ex.Message,
                });
            }
        }
        return found;
    }

    private void LoadPackage(Package package)
    {
        var manifest = package.Manifest!;
        var context = new PluginLoadContext(package.Directory, $"plugin:{package.Id}");
        try
        {
            var assembly = context.LoadFile(manifest.Assembly);
            package.Types.AddRange(assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && PluginInterfaces.Any(i => i.IsAssignableFrom(t))));
            if (package.Types.Count == 0)
                throw new InvalidDataException(
                    $"{manifest.Assembly} has no public class implementing a SolPnl plugin interface " +
                    $"({string.Join(", ", PluginInterfaces.Select(i => i.Name))}).");

            package.Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                .Split('+')[0] ?? assembly.GetName().Version?.ToString();
            _byAssembly[assembly] = package.Id;
            _contexts.Add(context);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadImageFormatException or TypeLoadException
                                       or ReflectionTypeLoadException or MissingMethodException or UnauthorizedAccessException)
        {
            package.Types.Clear();
            package.State = PluginPackageStates.Failed;
            package.Error = ex is ReflectionTypeLoadException or TypeLoadException or MissingMethodException or FileNotFoundException
                ? $"It doesn't work with this version of SolPnl: {ex.Message}"
                : ex.Message;
            context.Unload();
        }
    }

    /// <summary>Plugin interface → implementing class, for every class in a loaded package.</summary>
    public IEnumerable<(Type Service, Type Implementation)> Registrations =>
        from package in _packages
        where package.State == PluginPackageStates.Loaded
        from type in package.Types
        from service in PluginInterfaces
        where service.IsAssignableFrom(type)
        select (service, type);

    /// <summary>The package a plugin instance came from, or null for one SolPnl created itself.</summary>
    public string? PackageIdOf(object plugin) => _byAssembly.GetValueOrDefault(plugin.GetType().Assembly);

    /// <summary>Every package, with the ids of the running plugins it provides.</summary>
    public IReadOnlyList<PluginPackageInfo> List(IEnumerable<IPlugin> plugins)
    {
        var byPackage = plugins
            .Select(p => (Package: PackageIdOf(p), p.Id))
            .Where(p => p.Package is not null)
            .GroupBy(p => p.Package!)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Id).Distinct().ToList());

        lock (_gate)
        {
            return _packages
                .OrderBy(p => p.Name, StringComparer.CurrentCulture)
                .Select(p => new PluginPackageInfo(p.Id, p.Name, p.Description, p.Version, p.Source, p.State, p.Error,
                    byPackage.GetValueOrDefault(p.Id) ?? []))
                .ToList();
        }
    }

    /// <summary>
    /// Unpacks a package zip (with <c>plugin.json</c> at its top level, or in its only folder) into the plugins folder,
    /// replacing an added package with the same id. Takes effect on restart.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a valid package; the message says why.</exception>
    public async Task<PluginManifest> InstallAsync(Stream zip, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(AddedDirectory);
        var staging = Path.Combine(AddedDirectory, $".staging-{Guid.NewGuid():N}");
        var zipPath = staging + ".zip";
        try
        {
            await using (var file = File.Create(zipPath))
                await CopyWithLimitAsync(zip, file, ct);

            try
            {
                ZipFile.ExtractToDirectory(zipPath, staging);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("That file isn't a zip file.");
            }
            catch (IOException ex)
            {
                throw new InvalidDataException($"The package can't be unpacked: {ex.Message}");
            }

            var root = staging;
            if (!File.Exists(Path.Combine(root, PluginManifest.FileName)) &&
                System.IO.Directory.GetDirectories(root) is [var only] && System.IO.Directory.GetFiles(root).Length == 0)
                root = only;
            var manifest = PluginManifest.Read(root);

            lock (_gate)
            {
                var target = Path.Combine(AddedDirectory, manifest.Id);
                if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, recursive: true);
                System.IO.Directory.Move(root, target);

                // Adding a package again undoes removing the built-in one with its id.
                var removed = ReadRemoved();
                if (removed.Remove(manifest.Id)) WriteRemoved(removed);
            }
            return manifest;
        }
        finally
        {
            TryDelete(() => File.Delete(zipPath));
            TryDelete(() =>
            {
                if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
            });
        }
    }

    /// <summary>
    /// Removes a package: deletes an added one, and marks a built-in one as removed (so the next release doesn't bring
    /// it back). Takes effect on restart. Settings and history of its plugins and devices are kept.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such package.</exception>
    public void Remove(string id)
    {
        lock (_gate)
        {
            var package = _packages.FirstOrDefault(p => p.Id == id)
                ?? throw new KeyNotFoundException($"No plugin package '{id}' is installed.");

            if (package.Source == PluginPackageSources.Added)
                System.IO.Directory.Delete(package.Directory, recursive: true);

            // An added package can replace the built-in one with its id; removing it removes both.
            if (package.Source == PluginPackageSources.BuiltIn || BuiltInIds().Contains(id))
            {
                var removed = ReadRemoved();
                if (removed.Add(id)) WriteRemoved(removed);
            }
        }
    }

    /// <summary>Brings back a removed built-in package. Takes effect on restart.</summary>
    /// <exception cref="KeyNotFoundException">No removed built-in package with that id.</exception>
    public void Restore(string id)
    {
        lock (_gate)
        {
            var removed = ReadRemoved();
            if (!_packages.Any(p => p.Id == id && p.State == PluginPackageStates.Removed) || !removed.Remove(id))
                throw new KeyNotFoundException($"'{id}' isn't a removed built-in plugin.");
            WriteRemoved(removed);
        }
    }

    /// <summary>
    /// Adds <paramref name="current"/> (secret setting keys of the running plugins) to the keys remembered from earlier
    /// runs, and returns them all. Backups use this to leave out the passwords of plugins that have since been removed.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> RememberSecretKeys(IReadOnlyDictionary<string, IReadOnlySet<string>> current)
    {
        lock (_gate)
        {
            var path = Path.Combine(AddedDirectory, SecretKeysFile);
            Dictionary<string, SortedSet<string>> all;
            try
            {
                all = File.Exists(path)
                    ? JsonSerializer.Deserialize<Dictionary<string, SortedSet<string>>>(File.ReadAllText(path), Json) ?? []
                    : [];
            }
            catch (JsonException)
            {
                all = [];
            }

            var changed = false;
            foreach (var (plugin, keys) in current)
            {
                if (!all.TryGetValue(plugin, out var known)) all[plugin] = known = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var key in keys) changed |= known.Add(key);
            }
            if (changed)
            {
                System.IO.Directory.CreateDirectory(AddedDirectory);
                File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
            }
            return all.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value, StringComparer.Ordinal);
        }
    }

    private HashSet<string> BuiltInIds() =>
        Discover(BuiltInDirectory, PluginPackageSources.BuiltIn)
            .Where(p => p.Manifest is not null)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);

    private HashSet<string> ReadRemoved()
    {
        var path = Path.Combine(AddedDirectory, RemovedFile);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteRemoved(HashSet<string> removed)
    {
        System.IO.Directory.CreateDirectory(AddedDirectory);
        File.WriteAllText(Path.Combine(AddedDirectory, RemovedFile),
            JsonSerializer.Serialize(removed.Order(StringComparer.Ordinal), Json));
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxPackageBytes)
                throw new InvalidDataException($"The package is larger than {MaxPackageBytes / (1024 * 1024)} MB.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Lets the plugin assemblies be unloaded once nothing uses them (after the host that used them is disposed).</summary>
    public void Dispose()
    {
        foreach (var context in _contexts) context.Unload();
        _contexts.Clear();
    }
}

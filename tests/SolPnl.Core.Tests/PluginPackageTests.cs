using System.IO.Compression;
using System.Text.Json;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;
using SolPnl.Core.Plugins;
using SolPnl.Plugin.Simulator;

namespace SolPnl.Core.Tests;

/// <summary>Loads the real Simulator plugin from package folders, the way SolPnl loads plugins at startup.</summary>
public sealed class PluginPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solpnl-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly List<PluginPackages> _loaded = [];

    public PluginPackageTests() => Directory.CreateDirectory(_root);

    private string BuiltIn => Path.Combine(_root, "built-in");
    private string Added => Path.Combine(_root, "added");

    private static readonly string SimulatorDll = typeof(SimulatedInverterPluginType).Assembly.Location;

    public void Dispose()
    {
        foreach (var packages in _loaded) packages.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private PluginPackages Load(bool development = false)
    {
        var packages = PluginPackages.Load(BuiltIn, Added, development);
        _loaded.Add(packages);
        return packages;
    }

    private static string WritePackage(string parent, string folder, object manifest, bool withAssembly = true)
    {
        var dir = Path.Combine(parent, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName), JsonSerializer.Serialize(manifest));
        if (withAssembly) File.Copy(SimulatorDll, Path.Combine(dir, Path.GetFileName(SimulatorDll)));
        return dir;
    }

    private static object Manifest(string id = "test.simulator", bool developmentOnly = false) =>
        new { id, name = "Simulator", assembly = Path.GetFileName(SimulatorDll), developmentOnly };

    private string Zip(Action<ZipArchive> fill)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) fill(zip);
        return path;
    }

    private string PackageZip(string id)
    {
        var source = WritePackage(Path.Combine(_root, "src"), id, Manifest(id));
        var path = Path.Combine(_root, id + ".zip");
        ZipFile.CreateFromDirectory(source, path);
        return path;
    }

    private static async Task<PluginManifest> InstallAsync(PluginPackages packages, string zipPath)
    {
        await using var stream = File.OpenRead(zipPath);
        return await packages.InstallAsync(stream);
    }

    [Fact]
    public void Load_RegistersThePluginsInAPackage_InIsolation()
    {
        WritePackage(BuiltIn, "simulator", Manifest());

        var packages = Load();

        var (service, implementation) = Assert.Single(packages.Registrations);
        Assert.Equal(typeof(IDevicePluginType), service);
        // Loaded on its own, not the copy this test references; the plugin interfaces are shared with SolPnl.
        Assert.NotSame(typeof(SimulatedInverterPluginType).Assembly, implementation.Assembly);
        var plugin = (IDevicePluginType)Activator.CreateInstance(implementation)!;
        Assert.Equal(SimulatedInverterPluginType.PluginId, plugin.Id);

        var info = Assert.Single(packages.List([plugin]));
        Assert.Equal(("test.simulator", PluginPackageSources.BuiltIn, PluginPackageStates.Loaded), (info.Id, info.Source, info.State));
        Assert.Equal([SimulatedInverterPluginType.PluginId], info.PluginIds);
        Assert.NotNull(info.Version);
    }

    [Fact]
    public void Load_SkipsDevelopmentOnlyPackages_OutsideDevelopment()
    {
        WritePackage(BuiltIn, "simulator", Manifest(developmentOnly: true));

        Assert.Empty(Load().List([]));
        Assert.Single(Load(development: true).Registrations);
    }

    [Fact]
    public void Load_ListsAPackageThatCannotLoad_WithTheReason()
    {
        WritePackage(Added, "broken", Manifest("test.broken"), withAssembly: false);
        Directory.CreateDirectory(Path.Combine(Added, "no-manifest"));

        var listed = Load().List([]);

        Assert.All(listed, p => Assert.Equal(PluginPackageStates.Failed, p.State));
        Assert.Contains(listed, p => p.Id == "broken" && p.Error!.Contains("doesn't contain"));
        Assert.Contains(listed, p => p.Id == "no-manifest" && p.Error!.Contains(PluginManifest.FileName));
    }

    [Fact]
    public void RemovingABuiltInPackage_KeepsItOut_UntilRestored()
    {
        WritePackage(BuiltIn, "simulator", Manifest());

        Load().Remove("test.simulator");
        var removed = Load();
        Assert.Empty(removed.Registrations);
        Assert.Equal(PluginPackageStates.Removed, Assert.Single(removed.List([])).State);
        Assert.True(Directory.Exists(Path.Combine(BuiltIn, "simulator")), "the release's files are left alone");

        removed.Restore("test.simulator");
        Assert.Single(Load().Registrations);
    }

    [Fact]
    public async Task InstalledPackage_LoadsOnTheNextStart_AndRemovingDeletesIt()
    {
        var manifest = await InstallAsync(Load(), PackageZip("test.added"));
        Assert.Equal("test.added", manifest.Id);

        var packages = Load();
        Assert.Single(packages.Registrations);
        Assert.Equal(PluginPackageSources.Added, Assert.Single(packages.List([])).Source);

        packages.Remove("test.added");
        Assert.False(Directory.Exists(Path.Combine(Added, "test.added")));
        Assert.Empty(Load().List([]));
    }

    [Fact]
    public async Task AddedPackage_ReplacesTheBuiltInOneWithItsId()
    {
        WritePackage(BuiltIn, "simulator", Manifest());
        await InstallAsync(Load(), PackageZip("test.simulator"));

        var package = Assert.Single(Load().List([]));
        Assert.Equal(PluginPackageSources.Added, package.Source);
    }

    [Fact]
    public async Task Install_AcceptsAPackageInsideOneFolder()
    {
        var path = Zip(zip =>
        {
            zip.CreateEntryFromFile(SimulatorDll, "simulator/" + Path.GetFileName(SimulatorDll));
            using var writer = new StreamWriter(zip.CreateEntry("simulator/" + PluginManifest.FileName).Open());
            writer.Write(JsonSerializer.Serialize(Manifest("test.nested")));
        });

        Assert.Equal("test.nested", (await InstallAsync(Load(), path)).Id);
        Assert.True(File.Exists(Path.Combine(Added, "test.nested", PluginManifest.FileName)));
    }

    [Fact]
    public async Task Install_RejectsInvalidPackages_AndLeavesNothingBehind()
    {
        var packages = Load();
        var notZip = Path.Combine(_root, "notes.zip");
        File.WriteAllText(notZip, "not a zip");

        var noManifest = Zip(zip => zip.CreateEntryFromFile(SimulatorDll, Path.GetFileName(SimulatorDll)));
        var badId = Zip(zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry(PluginManifest.FileName).Open());
            writer.Write(JsonSerializer.Serialize(Manifest("../escape")));
        });
        var traversal = Zip(zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open());
            writer.Write("x");
        });

        Assert.Contains("isn't a zip", (await Assert.ThrowsAsync<InvalidDataException>(() => InstallAsync(packages, notZip))).Message);
        Assert.Contains(PluginManifest.FileName, (await Assert.ThrowsAsync<InvalidDataException>(() => InstallAsync(packages, noManifest))).Message);
        Assert.Contains("\"id\"", (await Assert.ThrowsAsync<InvalidDataException>(() => InstallAsync(packages, badId))).Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallAsync(packages, traversal));

        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Added));
    }

    [Fact]
    public void RememberSecretKeys_KeepsKeysOfPluginsThatAreGone()
    {
        var packages = Load();
        packages.RememberSecretKeys(new Dictionary<string, IReadOnlySet<string>> { ["email"] = new HashSet<string> { "password" } });

        var later = Load().RememberSecretKeys(new Dictionary<string, IReadOnlySet<string>> { ["other"] = new HashSet<string> { "key" } });

        Assert.Equal(["password"], later["email"]);
        Assert.Equal(["key"], later["other"]);
    }
}

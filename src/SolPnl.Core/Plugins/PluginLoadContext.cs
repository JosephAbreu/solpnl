using System.Reflection;
using System.Runtime.Loader;

namespace SolPnl.Core.Plugins;

/// <summary>
/// Loads one plugin package's assemblies in isolation. Assemblies SolPnl itself has (SolPnl.Contracts, the .NET and
/// Microsoft.Extensions libraries) come from SolPnl, so plugin and host share the plugin interfaces; everything else
/// (e.g. an email library) comes from the package folder. Files are read into memory, so they aren't locked and a
/// package can be replaced or deleted while SolPnl runs (the change applies on restart).
/// </summary>
internal sealed class PluginLoadContext(string directory, string name) : AssemblyLoadContext(name, isCollectible: true)
{
    private static readonly Lazy<HashSet<string>> HostAssemblies = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    public Assembly LoadFile(string fileName)
    {
        var path = Path.Combine(directory, fileName);
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } simpleName || HostAssemblies.Value.Contains(simpleName)) return null;
        var file = simpleName + ".dll";
        return File.Exists(Path.Combine(directory, file)) ? LoadFile(file) : null;
    }
}

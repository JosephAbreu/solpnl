namespace SolPnl.Contracts.Plugins;

/// <summary>Base contract shared by every SolPnl plugin.</summary>
public interface IPlugin
{
    /// <summary>Stable, unique identifier, e.g. "solarman-srne". Used in config and storage.</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string DisplayName { get; }
}

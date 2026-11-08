using SolPnl.Contracts.Models;

namespace SolPnl.Contracts.Plugins;

/// <summary>
/// A plugin with its own settings (not per device), e.g. a mail server or an API key. SolPnl stores the values and
/// shows a card on the Settings page built from <see cref="PluginSettings"/> while the plugin is enabled.
/// Values are passed in on every call, so plugins stay stateless.
/// </summary>
public interface IConfigurablePlugin : IPlugin
{
    IReadOnlyList<SettingField> PluginSettings { get; }

    /// <summary>Label of the test button on the settings card (e.g. "Send test email"), or null for no test.</summary>
    string? TestLabel { get; }

    /// <summary>Tries the settings (e.g. sends a test message). Called with unsaved values from the form.</summary>
    Task<PluginActionResult> TestAsync(IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken = default);
}

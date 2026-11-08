namespace SolPnl.Contracts.Plugins;

/// <summary>
/// A kind of device a plugin supports (e.g. "Solarman logger + SRNE inverter"). SolPnl keeps a list of configured
/// devices; for each one it calls <see cref="Create"/> with that device's settings to get a running
/// <see cref="IDevicePlugin"/> instance. <see cref="Settings"/> describes the settings so the UI can build the
/// "Add device" form without plugin-specific code.
/// </summary>
public interface IDevicePluginType : IPlugin
{
    /// <summary>One or two sentences shown when choosing a device type and on the Plugins page.</summary>
    string Description { get; }

    IReadOnlyList<SettingField> Settings { get; }

    /// <summary>
    /// Creates a device instance from its settings (keys from <see cref="Settings"/>; values are strings as entered).
    /// Must not perform I/O. Throws <see cref="ArgumentException"/> with a user-facing message for invalid settings.
    /// </summary>
    IDevicePlugin Create(IReadOnlyDictionary<string, string?> settings);
}

public enum SettingKind
{
    Text,
    Number,
    /// <summary>Never sent back to clients once saved (e.g. passwords, API keys).</summary>
    Secret,
    /// <summary>One of <see cref="SettingField.Options"/>.</summary>
    Choice,
}

/// <summary>Describes one setting of a device type or plugin, for validation and for building forms.</summary>
public sealed record SettingField(
    string Key,
    string Label,
    SettingKind Kind = SettingKind.Text,
    bool Required = true,
    string? Default = null,
    string? Placeholder = null,
    string? Help = null,
    IReadOnlyList<string>? Options = null);

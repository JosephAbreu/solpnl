using SolPnl.Contracts.Models;

namespace SolPnl.Contracts.Plugins;

/// <summary>
/// Delivers alerts raised by the core (email, SMS, ntfy, webhook, ...). Rules about what to send and when are
/// evaluated on the server; a notification plugin only handles delivery. Most also implement
/// <see cref="IConfigurablePlugin"/> for their connection settings, which are passed to every send.
/// </summary>
public interface INotificationPlugin : IPlugin
{
    string Description { get; }

    Task SendAsync(Alert alert, IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken = default);
}

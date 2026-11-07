using SolPnl.Contracts.Models;

namespace SolPnl.Contracts.Plugins;

/// <summary>
/// Delivers alerts raised by the core (SMS, email, ntfy, webhook, ...). Alert rules are evaluated
/// on the server; a notification plugin only handles delivery.
/// </summary>
public interface INotificationPlugin : IPlugin
{
    Task SendAsync(Alert alert, CancellationToken cancellationToken = default);
}

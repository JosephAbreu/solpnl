namespace SolPnl.Contracts.Models;

/// <summary>
/// What the SolPnl server is running, for the About page.
/// <see cref="Version"/> is the informational version ({year}.{month}.{build}, "+local" when run from source,
/// "+modified" when deployed with uncommitted changes); <see cref="Commit"/> is the git commit it was built from.
/// <see cref="Database"/> is null until storage is set up.
/// </summary>
public sealed record AboutInfo(
    string Version,
    string? Commit,
    string Runtime,
    string OperatingSystem,
    string? Database,
    DateTimeOffset StartedAt);

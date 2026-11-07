namespace SolPnl.Contracts.Models;

/// <summary>Whether SolPnl still needs first-run setup (no storage configured yet).</summary>
public sealed record SetupStatus(bool SetupRequired);

/// <summary>
/// Storage choice from the setup wizard. <see cref="Driver"/> is "sqlite", "mysql" (MySQL/MariaDB) or "mssql".
/// Either fill the individual fields, or set <see cref="ConnectionString"/> (advanced), which takes precedence.
/// </summary>
public sealed record StorageSetupRequest
{
    public required string Code { get; init; }
    public required string Driver { get; init; }

    /// <summary>SQLite only: database file path.</summary>
    public string? SqlitePath { get; init; }

    public string? Server { get; init; }
    public int? Port { get; init; }
    public string? Database { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }

    /// <summary>SQL Server only: accept the server's certificate without validating it (typical for a LAN server).</summary>
    public bool TrustServerCertificate { get; init; }

    /// <summary>Advanced: a full connection string; overrides the individual fields.</summary>
    public string? ConnectionString { get; init; }
}

/// <summary>Outcome of testing or saving a storage choice.</summary>
public sealed record SetupResult(bool Ok, string Message, string? ServerVersion = null);

/// <summary>Defaults the wizard pre-fills (e.g. where the SQLite file goes on this machine).</summary>
public sealed record SetupDefaults(string SqlitePath);

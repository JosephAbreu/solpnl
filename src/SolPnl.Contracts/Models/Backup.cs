namespace SolPnl.Contracts.Models;

/// <summary>
/// Backup schedule. <see cref="Time"/> is "HH:mm" in the site's time zone. <see cref="Folder"/> is null for the
/// default (a "backups" folder in SolPnl's data directory).
/// </summary>
public sealed record BackupSettings(bool Enabled = true, string Time = "02:30", int Keep = 14, string? Folder = null);

/// <summary>Why a backup was taken. Part of the file name.</summary>
public static class BackupKinds
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";

    /// <summary>Taken automatically just before a restore replaces the data.</summary>
    public const string BeforeRestore = "before-restore";
}

/// <summary>One backup file.</summary>
public sealed record BackupFile(string Name, DateTimeOffset CreatedAt, long SizeBytes, string Kind);

/// <summary>Outcome of the latest backup run or restore.</summary>
public sealed record BackupRunResult(bool Ok, DateTimeOffset At, string Message, string? Name = null, long? SizeBytes = null,
    double? Seconds = null);

/// <summary>Everything the Backups page shows.</summary>
public sealed record BackupStatus(
    BackupSettings Settings,
    string Folder,
    IReadOnlyList<BackupFile> Backups,
    BackupRunResult? LastBackup,
    DateTimeOffset? NextBackupAt,
    BackupRunResult? LastRestore,
    bool Running,
    bool RestorePending);

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;
using SolPnl.Core.Notifications;
using SolPnl.Core.Site;

namespace SolPnl.Core.Backup;

/// <summary>Where machine-specific files live (settings file, default SQLite database, backups).</summary>
public sealed record DataDirectory(string Path);

/// <summary>
/// Backups of the SolPnl database: on a daily schedule (in the site's time zone) and on demand, kept to the newest
/// N files, in a folder on the server (by default "backups" in the data directory).
///
/// Restores don't run while SolPnl is running: the backup is staged as restore-pending.zip in the data directory and
/// SolPnl restarts; on the way up, before polling starts, the current data is backed up ("before-restore") and then
/// replaced. If anything fails, the data is left as it was.
/// </summary>
public sealed partial class BackupService(
    IDbContextFactory<SolPnlDb> dbFactory,
    DbSettings dbSettings,
    MigrationService migrations,
    PluginCatalog plugins,
    SiteSettingsStore site,
    NotificationService notifications,
    DataDirectory dataDirectory,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    public const string SettingsKey = "backup.settings";
    private const string FailedAlertKey = "backup:failed";
    private const string StateFileName = "backup-state.json";
    private const string PendingRestoreName = "restore-pending.zip";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();

    private string StatePath => Path.Combine(dataDirectory.Path, StateFileName);
    public string PendingRestorePath => Path.Combine(dataDirectory.Path, PendingRestoreName);
    public string DefaultFolder => Path.Combine(dataDirectory.Path, "backups");

    private long CurrentSchema => migrations.GetScripts() is { Count: > 0 } scripts ? scripts.Max(s => s.Number) : 0;

    // ---- Settings and status -------------------------------------------------------------------------------

    public async Task<BackupSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.AppSettings.AsNoTracking().Where(s => s.Key == SettingsKey).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return json is null ? new BackupSettings() : JsonSerializer.Deserialize<BackupSettings>(json) ?? new BackupSettings();
    }

    /// <summary>Validates and saves the schedule. A custom folder must be writable. Throws <see cref="ArgumentException"/>.</summary>
    public async Task<BackupSettings> SaveSettingsAsync(BackupSettings settings, CancellationToken ct = default)
    {
        if (!TryParseTime(settings.Time, out _))
            throw new ArgumentException("Time must be a time of day such as 02:30.");
        if (settings.Keep is < 1 or > 365)
            throw new ArgumentException("Keep between 1 and 365 backups.");

        var folder = string.IsNullOrWhiteSpace(settings.Folder) ? null : settings.Folder.Trim();
        if (folder is not null)
        {
            if (!Path.IsPathFullyQualified(folder))
                throw new ArgumentException("The folder must be a full path on the SolPnl server, e.g. /mnt/nas/solpnl-backups.");
            CheckWritable(folder);
        }

        var saved = settings with { Time = NormalizeTime(settings.Time), Folder = folder };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == SettingsKey, ct);
        var json = JsonSerializer.Serialize(saved);
        if (row is null) db.AppSettings.Add(new AppSettingEntity { Key = SettingsKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
        return saved;
    }

    public async Task<BackupStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var folder = FolderFor(settings);
        var state = ReadState();
        DateTimeOffset? next = settings.Enabled
            ? NextRun(settings, await ZoneAsync(ct), state.LastScheduledAt, time.GetUtcNow())
            : null;
        return new BackupStatus(settings, folder, List(folder), state.LastBackup, next, state.LastRestore,
            Running: _gate.CurrentCount == 0, RestorePending: File.Exists(PendingRestorePath));
    }

    /// <summary>Backups in the folder, newest first.</summary>
    public static IReadOnlyList<BackupFile> List(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles("solpnl-*.zip")
            .Select(f => ParseName(f.Name) is { } parsed ? new BackupFile(f.Name, parsed.CreatedAt, f.Length, parsed.Kind) : null)
            .OfType<BackupFile>()
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    public string FolderFor(BackupSettings settings) => settings.Folder ?? DefaultFolder;

    // ---- Running backups -----------------------------------------------------------------------------------

    /// <summary>Takes a backup now. Never throws for backup failures: the result says what happened.</summary>
    public async Task<BackupRunResult> RunAsync(string kind, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return new BackupRunResult(false, time.GetUtcNow(), "A backup is already running. Try again when it's finished.");
        try
        {
            return await RunLockedAsync(kind, await GetSettingsAsync(ct), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a backup in the background; false if one is already running.</summary>
    public bool TryStart(string kind, CancellationToken ct)
    {
        if (!_gate.Wait(0, CancellationToken.None)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunLockedAsync(kind, await GetSettingsAsync(ct), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup failed.");
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None);
        return true;
    }

    private async Task<BackupRunResult> RunLockedAsync(string kind, BackupSettings settings, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var at = time.GetUtcNow();
        var folder = FolderFor(settings);
        var name = FileName(at, kind);
        var path = Path.Combine(folder, name);
        var partial = path + ".partial";
        BackupRunResult result;
        try
        {
            Directory.CreateDirectory(folder);
            BackupManifest manifest;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                manifest = await BackupArchive.WriteAsync(dbFactory, dbSettings, CurrentSchema,
                    new SecretKeys(plugins.SecretSettingKeys()), file, time, ct);
            }
            File.Move(partial, path);
            var size = new FileInfo(path).Length;
            var rows = manifest.Tables.Values.Sum();
            result = new BackupRunResult(true, at, $"Backed up {rows.ToString("N0", CultureInfo.InvariantCulture)} rows.", name, size,
                Math.Round(started.Elapsed.TotalSeconds, 1));
            logger.LogInformation("Backup {Name} written ({Rows} rows, {Size} bytes, {Seconds:N1} s).", name, rows, size, started.Elapsed.TotalSeconds);
            ApplyRetention(folder, settings.Keep);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TryDelete(partial);
            logger.LogWarning(ex, "Backup to {Folder} failed.", folder);
            result = new BackupRunResult(false, at, $"Backup failed: {ex.Message}", Seconds: Math.Round(started.Elapsed.TotalSeconds, 1));
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        UpdateState(s => s with { LastBackup = result });
        return result;
    }

    /// <summary>Deletes the oldest backups beyond <paramref name="keep"/>.</summary>
    private void ApplyRetention(string folder, int keep)
    {
        foreach (var old in List(folder).Skip(keep))
        {
            try
            {
                File.Delete(Path.Combine(folder, old.Name));
                logger.LogInformation("Deleted old backup {Name}.", old.Name);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Couldn't delete old backup {Name}.", old.Name);
            }
        }
    }

    /// <summary>Runs the scheduled backup if it's due. Called every minute by <see cref="BackupBackgroundService"/>.</summary>
    public async Task RunScheduledIfDueAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.Enabled) return;
        var now = time.GetUtcNow();
        if (NextRun(settings, await ZoneAsync(ct), ReadState().LastScheduledAt, now) > now) return;

        // A manual backup is running: try again next minute (no "failed" alert for that).
        if (!await _gate.WaitAsync(0, ct)) return;
        BackupRunResult result;
        try
        {
            // Recorded before running, so a failing backup is retried tomorrow, not every minute.
            UpdateState(s => s with { LastScheduledAt = now });
            result = await RunLockedAsync(BackupKinds.Scheduled, settings, ct);
        }
        finally
        {
            _gate.Release();
        }
        if (result.Ok)
        {
            await notifications.ClearSystemAsync(FailedAlertKey, "Backups are working again",
                $"The scheduled backup {result.Name} completed.", ct);
        }
        else
        {
            await notifications.RaiseSystemAsync(FailedAlertKey, AlertSeverity.Warning, "Backup failed",
                $"{result.Message} The backup folder is {FolderFor(settings)}. SolPnl will try again at the next scheduled time; " +
                "you can also run one from the Backups page.", p => p.BackupFailed, ct);
        }
    }

    /// <summary>
    /// When the next scheduled backup is due: today's time in <paramref name="zone"/>, or tomorrow's if today's
    /// already ran. A missed time (SolPnl was off) is due immediately.
    /// </summary>
    public static DateTimeOffset NextRun(BackupSettings settings, TimeZoneInfo zone, DateTimeOffset? lastScheduledAt, DateTimeOffset now)
    {
        TryParseTime(settings.Time, out var timeOfDay);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var due = At(today, timeOfDay, zone);
        // Already ran at or after today's time: tomorrow. Otherwise today's (now, if that time has passed).
        return lastScheduledAt is { } last && last >= due ? At(today.AddDays(1), timeOfDay, zone) : due;
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly timeOfDay, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(timeOfDay, DateTimeKind.Unspecified);
        // A time skipped by a daylight-saving change (e.g. 02:30 on the spring-forward day) runs an hour later.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) =>
        await site.GetLocationAsync(ct) is { } location ? SiteSettingsStore.Zone(location) : TimeZoneInfo.Local;

    // ---- Files ---------------------------------------------------------------------------------------------

    /// <summary>Opens a backup for download, or null if there is no such backup.</summary>
    public async Task<FileStream?> OpenAsync(string name, CancellationToken ct = default)
    {
        var path = await PathOfAsync(name, ct);
        return path is null ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        var path = await PathOfAsync(name, ct);
        if (path is null) return false;
        File.Delete(path);
        logger.LogInformation("Backup {Name} deleted.", name);
        return true;
    }

    /// <summary>Full path of a backup in the current folder; null for unknown names (and anything that isn't a backup name).</summary>
    private async Task<string?> PathOfAsync(string name, CancellationToken ct)
    {
        if (ParseName(name) is null) return null;
        var path = Path.Combine(FolderFor(await GetSettingsAsync(ct)), name);
        return File.Exists(path) ? path : null;
    }

    // ---- Restore -------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks a backup and stages it to be restored when SolPnl next starts. The caller restarts SolPnl.
    /// Throws <see cref="InvalidDataException"/> if it isn't a backup this version can restore.
    /// </summary>
    public async Task<BackupManifest> StageRestoreAsync(Stream backup, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dataDirectory.Path);
        var staging = PendingRestorePath + ".partial";
        try
        {
            await using (var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await backup.CopyToAsync(file, ct);
            var manifest = BackupArchive.ReadManifest(staging, CurrentSchema);
            File.Move(staging, PendingRestorePath, overwrite: true);
            logger.LogInformation("Restore of a backup from {CreatedAt} staged; it runs when SolPnl restarts.", manifest.CreatedAt);
            return manifest;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Stages one of the backups in the folder (see <see cref="StageRestoreAsync(Stream, CancellationToken)"/>).</summary>
    public async Task<BackupManifest?> StageRestoreAsync(string name, CancellationToken ct = default)
    {
        await using var file = await OpenAsync(name, ct);
        return file is null ? null : await StageRestoreAsync(file, ct);
    }

    /// <summary>
    /// Runs a staged restore, if there is one. Called at startup after the schema is up to date and before anything
    /// else uses the database. Never throws: a failed restore leaves the data as it was and is reported on the
    /// Backups page.
    /// </summary>
    public async Task ApplyPendingRestoreAsync(CancellationToken ct = default)
    {
        if (!File.Exists(PendingRestorePath)) return;
        var started = Stopwatch.StartNew();
        BackupRunResult result;
        try
        {
            logger.LogWarning("Restoring a backup. Backing up the current data first.");
            var safety = await RunAsync(BackupKinds.BeforeRestore, ct);
            if (!safety.Ok)
            {
                result = new BackupRunResult(false, time.GetUtcNow(),
                    $"The restore didn't run because the current data couldn't be backed up first. {safety.Message}");
            }
            else
            {
                var manifest = await BackupArchive.RestoreAsync(dbFactory, dbSettings, CurrentSchema,
                    new SecretKeys(plugins.SecretSettingKeys()), PendingRestorePath, ct);
                var rows = manifest.Tables.Values.Sum();
                var from = manifest.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
                result = new BackupRunResult(true, time.GetUtcNow(),
                    $"Restored the backup from {from} ({rows.ToString("N0", CultureInfo.InvariantCulture)} rows). " +
                    $"The data from before the restore is in {safety.Name}. Passwords aren't in backups; any that weren't " +
                    "already set on this server need to be entered again.",
                    safety.Name, Seconds: Math.Round(started.Elapsed.TotalSeconds, 1));
                logger.LogWarning("Backup from {From} restored ({Rows} rows) in {Seconds:N1} s.", from, rows, started.Elapsed.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore failed; the data was left as it was.");
            result = new BackupRunResult(false, time.GetUtcNow(), $"The restore failed and nothing was changed: {ex.Message}",
                Seconds: Math.Round(started.Elapsed.TotalSeconds, 1));
        }

        TryDelete(PendingRestorePath);
        UpdateState(s => s with { LastRestore = result });
    }

    // ---- Names and state -----------------------------------------------------------------------------------

    public static string FileName(DateTimeOffset at, string kind) =>
        $"solpnl-{at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{kind}.zip";

    public static (DateTimeOffset CreatedAt, string Kind)? ParseName(string name)
    {
        var match = BackupName().Match(name);
        if (!match.Success) return null;
        var at = DateTime.ParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return (new DateTimeOffset(at, TimeSpan.Zero), match.Groups[2].Value);
    }

    [GeneratedRegex(@"^solpnl-(\d{8}T\d{6}Z)-([a-z]+(?:-[a-z]+)*)\.zip$")]
    private static partial Regex BackupName();

    private static bool TryParseTime(string? text, out TimeOnly value) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static string NormalizeTime(string text)
    {
        TryParseTime(text, out var value);
        return value.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static void CheckWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".solpnl-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"SolPnl can't write to {folder}: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Last results, kept in a file rather than the database so they survive a restore.</summary>
    private sealed record State(BackupRunResult? LastBackup = null, BackupRunResult? LastRestore = null, DateTimeOffset? LastScheduledAt = null);

    private State ReadState()
    {
        lock (_stateLock)
        {
            try
            {
                return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State() : new State();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "Couldn't read {Path}; starting fresh.", StatePath);
                return new State();
            }
        }
    }

    private void UpdateState(Func<State, State> change)
    {
        lock (_stateLock)
        {
            var state = change(ReadState());
            Directory.CreateDirectory(dataDirectory.Path);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, StatePath, overwrite: true);
        }
    }
}

/// <summary>Checks every minute whether the scheduled backup is due.</summary>
public sealed class BackupBackgroundService(BackupService backups, ILogger<BackupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup work (polling, weather) settle first.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await backups.RunScheduledIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Checking the backup schedule failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

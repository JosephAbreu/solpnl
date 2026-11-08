using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SolPnl.Core.Data;

namespace SolPnl.Core.Backup;

/// <summary>
/// Describes a backup file. <see cref="Schema"/> is the number of the newest schema script the data was taken at;
/// a backup can be restored by a SolPnl whose schema is the same or newer.
/// </summary>
public sealed record BackupManifest(
    int Format,
    string Version,
    DateTimeOffset CreatedAt,
    string Driver,
    long Schema,
    Dictionary<string, long> Tables,
    bool SecretsIncluded);

/// <summary>Secret setting keys by plugin id (see <see cref="Site.PluginCatalog.SecretSettingKeys"/>).</summary>
public sealed class SecretKeys(IReadOnlyDictionary<string, IReadOnlySet<string>> byPlugin)
{
    public static readonly SecretKeys None = new(new Dictionary<string, IReadOnlySet<string>>());

    public bool IsSecret(string pluginId, string key) => byPlugin.TryGetValue(pluginId, out var keys) && keys.Contains(key);

    public IReadOnlySet<string> For(string pluginId) =>
        byPlugin.TryGetValue(pluginId, out var keys) ? keys : new HashSet<string>();
}

/// <summary>
/// The backup file format: a zip with manifest.json and one JSON-lines file per table (&lt;table&gt;.jsonl), one entity
/// per line. Rows go through the EF model rather than the database's own dump tool, so a backup restores into any
/// supported database (SQLite, MySQL/MariaDB, SQL Server), not just the kind it came from.
///
/// Secret setting values (e.g. the email password) are never written: backup files can be downloaded and copied
/// around, and SolPnl has no login yet. A restore keeps the secrets already set on the machine it runs on.
/// </summary>
public static class BackupArchive
{
    public const int Format = 1;
    public const string ManifestName = "manifest.json";
    private const int BatchSize = 2_000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Every table, parents first: the order rows are restored in. Backups write them in reverse (children first),
    /// so a device or metric added while a backup runs is always present for the readings that refer to it.
    /// </summary>
    public static readonly IReadOnlyList<IBackupTable> Tables =
    [
        new BackupTable<DeviceEntity>(),
        new BackupTable<MetricEntity>(),
        new BackupTable<ReadingEntity>(),
        new BackupTable<LatestReadingEntity>(),
        new BackupTable<RollupEntity>(),
        new BackupTable<RollupStateEntity>(),
        new BackupTable<DeviceConfigEntity>(StripDeviceSecrets),
        new BackupTable<PluginStateEntity>(),
        new BackupTable<AppSettingEntity>(),
        new BackupTable<DaySummaryEntity>(),
        new BackupTable<PluginSettingEntity>((row, secrets) => secrets.IsSecret(row.PluginId, row.Key) ? null : row),
        new BackupTable<NotificationLogEntity>(),
        new BackupTable<AlertHistoryEntity>(),
        new BackupTable<GridEventEntity>(),
    ];

    public static string Version { get; } =
        typeof(BackupArchive).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Writes a backup of the whole database to <paramref name="output"/> (a zip).</summary>
    public static async Task<BackupManifest> WriteAsync(IDbContextFactory<SolPnlDb> dbFactory, DbSettings settings, long schema,
        SecretKeys secrets, Stream output, TimeProvider time, CancellationToken ct)
    {
        var createdAt = time.GetUtcNow();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
            foreach (var table in Tables.Reverse())
            {
                var name = table.TableName(db);
                var entry = zip.CreateEntry($"{name}.jsonl", CompressionLevel.Optimal);
                await using var stream = new BufferedStream(entry.Open(), 64 * 1024);
                counts[name] = await table.ExportAsync(db, stream, secrets, ct);
            }

            var manifest = new BackupManifest(Format, Version, createdAt, settings.DriverName, schema, counts, SecretsIncluded: false);
            var manifestEntry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            await using (var stream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true }, ct);
            return manifest;
        }
    }

    /// <summary>Reads and checks a backup's manifest. Throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public static BackupManifest ReadManifest(ZipArchive zip, long currentSchema)
    {
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("This isn't a SolPnl backup (no manifest.json).");
        BackupManifest? manifest;
        using (var stream = entry.Open())
        {
            try
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(stream);
            }
            catch (JsonException)
            {
                manifest = null;
            }
        }
        if (manifest is null) throw new InvalidDataException("This backup's manifest.json can't be read.");
        if (manifest.Format != Format)
            throw new InvalidDataException($"This backup uses format {manifest.Format}; this version of SolPnl reads format {Format}.");
        if (manifest.Schema > currentSchema)
            throw new InvalidDataException(
                $"This backup is from a newer version of SolPnl ({manifest.Version}). Update SolPnl, then restore it.");
        return manifest;
    }

    /// <summary>Reads and checks the manifest of a backup file.</summary>
    public static BackupManifest ReadManifest(string path, long currentSchema)
    {
        using var zip = OpenZip(path);
        return ReadManifest(zip, currentSchema);
    }

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("This isn't a SolPnl backup (not a zip file).", ex);
        }
    }

    /// <summary>
    /// Replaces all data with the backup's, in one transaction: if anything fails, nothing changes. Secret settings
    /// and the backup schedule already on this machine are kept.
    /// </summary>
    public static async Task<BackupManifest> RestoreAsync(IDbContextFactory<SolPnlDb> dbFactory, DbSettings settings, long schema,
        SecretKeys secrets, string path, CancellationToken ct)
    {
        using var zip = OpenZip(path);
        var manifest = ReadManifest(zip, schema);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var kept = await KeptValues.ReadAsync(db, secrets, ct);

        foreach (var table in Tables.Reverse())
            await db.Database.ExecuteSqlRawAsync($"delete from {table.TableName(db)}", ct);

        foreach (var table in Tables)
        {
            var name = table.TableName(db);
            var entry = zip.GetEntry($"{name}.jsonl");
            if (entry is null) continue; // a table the backup's (older) schema didn't have yet

            var identity = settings.Driver == DbDriver.SqlServer && table.HasIdentityKey(db);
            if (identity) await db.Database.ExecuteSqlRawAsync($"set identity_insert {name} on", ct);
            await using (var stream = entry.Open())
                await table.ImportAsync(db, stream, ct);
            if (identity) await db.Database.ExecuteSqlRawAsync($"set identity_insert {name} off", ct);
        }

        await kept.ApplyAsync(db, ct);
        await transaction.CommitAsync(ct);
        return manifest;
    }

    private static DeviceConfigEntity StripDeviceSecrets(DeviceConfigEntity row, SecretKeys secrets)
    {
        var keys = secrets.For(row.PluginId);
        if (keys.Count == 0) return row;
        var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.SettingsJson) ?? [];
        if (!values.Keys.Any(keys.Contains)) return row;
        foreach (var key in keys) values.Remove(key);
        return new DeviceConfigEntity
        {
            Id = row.Id, Name = row.Name, PluginId = row.PluginId, DeviceKey = row.DeviceKey, CreatedAtMs = row.CreatedAtMs,
            SettingsJson = JsonSerializer.Serialize(values),
        };
    }

    /// <summary>Values on this machine that a restore must not replace: secrets (never in backups) and the schedule.</summary>
    private sealed class KeptValues
    {
        private List<PluginSettingEntity> _pluginSecrets = [];
        private Dictionary<string, Dictionary<string, string?>> _deviceSecrets = [];
        private string? _backupSettings;

        public static async Task<KeptValues> ReadAsync(SolPnlDb db, SecretKeys secrets, CancellationToken ct)
        {
            var kept = new KeptValues();
            kept._pluginSecrets = (await db.PluginSettings.AsNoTracking().ToListAsync(ct))
                .Where(s => secrets.IsSecret(s.PluginId, s.Key)).ToList();

            foreach (var device in await db.DeviceConfigs.AsNoTracking().ToListAsync(ct))
            {
                var keys = secrets.For(device.PluginId);
                if (keys.Count == 0) continue;
                var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(device.SettingsJson) ?? [];
                var secretValues = values.Where(v => keys.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);
                if (secretValues.Count > 0) kept._deviceSecrets[device.DeviceKey] = secretValues;
            }

            kept._backupSettings = await db.AppSettings.AsNoTracking().Where(s => s.Key == BackupService.SettingsKey)
                .Select(s => s.Value).SingleOrDefaultAsync(ct);
            return kept;
        }

        public async Task ApplyAsync(SolPnlDb db, CancellationToken ct)
        {
            db.ChangeTracker.AutoDetectChangesEnabled = true;
            foreach (var secret in _pluginSecrets)
            {
                var row = await db.PluginSettings.SingleOrDefaultAsync(s => s.PluginId == secret.PluginId && s.Key == secret.Key, ct);
                if (row is null) db.PluginSettings.Add(secret);
            }

            foreach (var device in await db.DeviceConfigs.Where(d => _deviceSecrets.Keys.Contains(d.DeviceKey)).ToListAsync(ct))
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(device.SettingsJson) ?? [];
                foreach (var (key, value) in _deviceSecrets[device.DeviceKey]) values.TryAdd(key, value);
                device.SettingsJson = JsonSerializer.Serialize(values);
            }

            var schedule = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == BackupService.SettingsKey, ct);
            if (_backupSettings is null)
            {
                if (schedule is not null) db.AppSettings.Remove(schedule);
            }
            else if (schedule is null) db.AppSettings.Add(new AppSettingEntity { Key = BackupService.SettingsKey, Value = _backupSettings });
            else schedule.Value = _backupSettings;

            await db.SaveChangesAsync(ct);
        }
    }

    public interface IBackupTable
    {
        Type EntityType { get; }
        string TableName(SolPnlDb db);
        bool HasIdentityKey(SolPnlDb db);
        Task<long> ExportAsync(SolPnlDb db, Stream output, SecretKeys secrets, CancellationToken ct);
        Task<long> ImportAsync(SolPnlDb db, Stream input, CancellationToken ct);
    }

    private sealed class BackupTable<T>(Func<T, SecretKeys, T?>? strip = null) : IBackupTable where T : class
    {
        public Type EntityType => typeof(T);

        private static IEntityType Model(SolPnlDb db) =>
            db.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} isn't in the model.");

        public string TableName(SolPnlDb db) => Model(db).GetTableName()!;

        /// <summary>A single generated integer key (an identity column on SQL Server).</summary>
        public bool HasIdentityKey(SolPnlDb db) =>
            Model(db).FindPrimaryKey() is { Properties: [var key] } && key.ValueGenerated == ValueGenerated.OnAdd
            && (key.ClrType == typeof(int) || key.ClrType == typeof(long));

        public async Task<long> ExportAsync(SolPnlDb db, Stream output, SecretKeys secrets, CancellationToken ct)
        {
            long count = 0;
            await using var writer = new Utf8JsonWriter(output);
            await foreach (var row in db.Set<T>().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
            {
                var value = strip is null ? row : strip(row, secrets);
                if (value is null) continue;
                writer.Reset();
                JsonSerializer.Serialize(writer, value, Json);
                await writer.FlushAsync(ct);
                output.WriteByte((byte)'\n');
                count++;
            }
            return count;
        }

        public async Task<long> ImportAsync(SolPnlDb db, Stream input, CancellationToken ct)
        {
            using var reader = new StreamReader(input);
            var batch = new List<T>(BatchSize);
            long count = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                batch.Add(JsonSerializer.Deserialize<T>(line, Json)
                          ?? throw new InvalidDataException($"Row {count + batch.Count + 1} of {TableName(db)} is empty."));
                if (batch.Count == BatchSize)
                {
                    await SaveAsync(db, batch, ct);
                    count += batch.Count;
                    batch.Clear();
                }
            }
            await SaveAsync(db, batch, ct);
            return count + batch.Count;
        }

        private static async Task SaveAsync(SolPnlDb db, List<T> batch, CancellationToken ct)
        {
            if (batch.Count == 0) return;
            db.Set<T>().AddRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }
}

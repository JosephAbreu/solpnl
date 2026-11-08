using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SolPnl.Core.Data;

/// <summary>
/// Applies the numbered, per-driver schema scripts embedded under Data/Migrations/&lt;driver&gt;/&lt;n&gt;.&lt;driver&gt;
/// and records each applied script in the schema_script table (pattern carried over from mqs).
///
/// Rules for scripts:
///  - Never edit a script once it has shipped; add the next number instead.
///  - Every change needs a script for each driver (sqlite, mysql, mssql) with the same number.
///  - MySQL/MariaDB commit DDL implicitly, so a DDL script that fails part-way can leave earlier statements
///    applied. Keep MySQL scripts re-runnable (if not exists / if exists) where possible.
/// </summary>
public sealed class MigrationService(IDbContextFactory<SolPnlDb> dbFactory, DbSettings settings, ILogger<MigrationService> logger)
{
    private const string ScriptTable = "schema_script";
    private const string LockName = "solpnl:migrate";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(5);

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            // Write-ahead logging lets reads (a backup, a long history query) run alongside polling's writes instead
            // of blocking them. It's a property of the database file, so this only changes something once.
            if (settings.Driver == DbDriver.Sqlite)
            {
                await using var wal = Command(connection, "pragma journal_mode=wal;");
                await wal.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureScriptTableAsync(connection, cancellationToken);
            var scripts = GetScripts();

            // Fast path (almost every start): nothing pending, so no lock is taken.
            if ((await GetPendingAsync(connection, scripts, cancellationToken)).Count == 0)
            {
                logger.LogDebug("Database schema is up to date ({Driver}).", settings.DriverName);
                return;
            }

            await AcquireLockAsync(connection, cancellationToken);
            try
            {
                // Re-read under the lock: another process may have applied them while this one waited.
                foreach (var script in await GetPendingAsync(connection, scripts, cancellationToken))
                    await ApplyAsync(connection, script, cancellationToken);
            }
            finally
            {
                await ReleaseLockAsync(connection);
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>Script names for the configured driver, in numeric order (e.g. "1.sqlite", "2.sqlite").</summary>
    public IReadOnlyList<Script> GetScripts()
    {
        var assembly = typeof(MigrationService).Assembly;
        var prefix = $"{typeof(MigrationService).Namespace}.Migrations.{settings.DriverName}.";
        var extension = $".{settings.DriverName}";

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(extension, StringComparison.Ordinal))
            .Select(n => new Script(n[prefix.Length..], n))
            .OrderBy(s => s.Number)
            .ToList();
    }

    public sealed record Script(string Name, string ResourceName)
    {
        public long Number { get; } = long.Parse(Name.Split('.')[0]);
    }

    private async Task EnsureScriptTableAsync(DbConnection connection, CancellationToken ct)
    {
        var sql = settings.Driver == DbDriver.SqlServer
            ? $"if object_id('{ScriptTable}', 'U') is null create table {ScriptTable} ( script varchar(500) not null primary key );"
            : $"create table if not exists {ScriptTable} ( script varchar(500) not null primary key );";

        try
        {
            await using var cmd = Command(connection, sql);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException) when (settings.Driver == DbDriver.SqlServer)
        {
            // The check-then-create isn't atomic on SQL Server; another process may have created it first.
            if (!await ScriptTableExistsAsync(connection, ct))
                throw;
        }
    }

    private async Task<bool> ScriptTableExistsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = Command(connection, $"select case when object_id('{ScriptTable}', 'U') is null then 0 else 1 end;");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private async Task<List<Script>> GetPendingAsync(DbConnection connection, IReadOnlyList<Script> scripts, CancellationToken ct)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = Command(connection, $"select script from {ScriptTable};"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                applied.Add(reader.GetString(0));
        }
        return scripts.Where(s => !applied.Contains(s.Name)).ToList();
    }

    private async Task ApplyAsync(DbConnection connection, Script script, CancellationToken ct)
    {
        logger.LogInformation("Applying schema script {Script} ({Driver}).", script.Name, settings.DriverName);

        await using var stream = typeof(MigrationService).Assembly.GetManifestResourceStream(script.ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded script {script.ResourceName}.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);

        // The script and the row recording it run in one transaction, so a script is never left applied but
        // unrecorded. SQLite and SQL Server DDL is transactional; MySQL/MariaDB DDL commits implicitly (see class docs).
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!string.IsNullOrWhiteSpace(sql))
        {
            await using var cmd = Command(connection, sql, transaction);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var record = Command(connection, $"insert into {ScriptTable} ( script ) values ( @script );", transaction,
                         ("@script", script.Name)))
        {
            await record.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    // Session-scoped lock, so it is released with the session if the process dies.
    // SQLite gets none: the database is a local file owned by a single SolPnl process, and each script
    // already runs in its own transaction.
    private async Task AcquireLockAsync(DbConnection connection, CancellationToken ct)
    {
        bool acquired;
        switch (settings.Driver)
        {
            case DbDriver.MySql:
            {
                await using var cmd = Command(connection, "select get_lock(@name, @timeout);", null,
                    ("@name", LockName), ("@timeout", (int)LockTimeout.TotalSeconds));
                cmd.CommandTimeout = (int)LockTimeout.TotalSeconds + 30; // the wait happens server-side
                var result = await cmd.ExecuteScalarAsync(ct);
                acquired = result is not null and not DBNull && Convert.ToInt32(result) == 1;
                break;
            }
            case DbDriver.SqlServer:
            {
                await using var cmd = Command(connection,
                    "declare @result int; " +
                    "exec @result = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout; " +
                    "select @result;", null,
                    ("@name", LockName), ("@timeout", (int)LockTimeout.TotalMilliseconds));
                cmd.CommandTimeout = (int)LockTimeout.TotalSeconds + 30;
                // 0 = granted immediately, 1 = granted after waiting; negative = timeout/deadlock/error.
                acquired = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) >= 0;
                break;
            }
            default:
                return;
        }

        if (!acquired)
            throw new InvalidOperationException("The database schema is being updated by another process. Try again in a minute.");
    }

    // Best effort: a failure here must not hide the error that caused the release.
    private async Task ReleaseLockAsync(DbConnection connection)
    {
        try
        {
            var sql = settings.Driver switch
            {
                DbDriver.MySql => "select release_lock(@name);",
                DbDriver.SqlServer => "exec sp_releaseapplock @Resource = @name, @LockOwner = 'Session';",
                _ => null,
            };
            if (sql is null) return;

            await using var cmd = Command(connection, sql, null, ("@name", LockName));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to release migration lock {LockName}.", LockName);
        }
    }

    private static DbCommand Command(DbConnection connection, string sql, DbTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}

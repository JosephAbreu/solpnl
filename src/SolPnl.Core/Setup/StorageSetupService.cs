using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;

namespace SolPnl.Core.Setup;

/// <summary>
/// Turns a setup-wizard storage choice into <see cref="DbSettings"/>, tests it, and applies it
/// (creates the schema and saves the "Db" section to the local settings file).
/// </summary>
public sealed class StorageSetupService(LocalSettingsFile settingsFile, ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>Builds settings from the request. Throws <see cref="ArgumentException"/> for missing/invalid input.</summary>
    public static DbSettings BuildSettings(StorageSetupRequest request)
    {
        DbDriver driver;
        try
        {
            driver = DbSettings.ParseDriver(request.Driver);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message, nameof(request), ex);
        }

        if (!string.IsNullOrWhiteSpace(request.ConnectionString))
            return new DbSettings(driver, request.ConnectionString.Trim());

        switch (driver)
        {
            case DbDriver.Sqlite:
            {
                var path = Required(request.SqlitePath, "Database file");
                return new DbSettings(driver, new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
            }
            case DbDriver.MySql:
            {
                var builder = new MySqlConnectionStringBuilder
                {
                    Server = Required(request.Server, "Server"),
                    Port = (uint)(request.Port ?? 3306),
                    Database = Required(request.Database, "Database"),
                    UserID = Required(request.User, "User"),
                    Password = request.Password ?? "",
                };
                return new DbSettings(driver, builder.ConnectionString);
            }
            default:
            {
                var server = Required(request.Server, "Server");
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = request.Port is { } port ? $"{server},{port}" : server,
                    InitialCatalog = Required(request.Database, "Database"),
                    UserID = Required(request.User, "User"),
                    Password = request.Password ?? "",
                    TrustServerCertificate = request.TrustServerCertificate,
                };
                return new DbSettings(driver, builder.ConnectionString);
            }
        }
    }

    /// <summary>Opens a connection and reports the server version, or the error.</summary>
    public static async Task<SetupResult> TestAsync(DbSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            if (settings.Driver == DbDriver.Sqlite)
                EnsureSqliteDirectory(settings.ConnectionString);

            await using var db = new SolPnlDbFactory(settings).CreateDbContext();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            var version = settings.Driver == DbDriver.Sqlite ? $"SQLite {connection.ServerVersion}" : connection.ServerVersion;
            await connection.CloseAsync();
            return new SetupResult(true, "Connection succeeded.", version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SetupResult(false, ex.GetBaseException().Message);
        }
    }

    /// <summary>Tests the connection, creates/updates the schema, then saves the storage choice.</summary>
    public async Task<SetupResult> ApplyAsync(DbSettings settings, CancellationToken cancellationToken = default)
    {
        var test = await TestAsync(settings, cancellationToken);
        if (!test.Ok) return test;

        try
        {
            var migrations = new MigrationService(new SolPnlDbFactory(settings), settings, _loggerFactory.CreateLogger<MigrationService>());
            await migrations.MigrateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SetupResult(false, $"Connected, but creating the database schema failed: {ex.GetBaseException().Message}", test.ServerVersion);
        }

        await settingsFile.SetSectionAsync("Db", new JsonObject
        {
            ["DriverName"] = settings.DriverName,
            ["ConnectionString"] = settings.ConnectionString,
        }, cancellationToken);

        return new SetupResult(true, "Storage is set up.", test.ServerVersion);
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{field} is required.") : value.Trim();

    private static void EnsureSqliteDirectory(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrEmpty(dataSource) || dataSource == ":memory:") return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }
}

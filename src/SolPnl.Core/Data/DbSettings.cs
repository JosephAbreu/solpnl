using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace SolPnl.Core.Data;

public enum DbDriver
{
    Sqlite,
    MySql,     // MySQL and MariaDB
    SqlServer,
}

/// <summary>
/// Database selection, read from configuration:
/// <code>
/// "Db": { "DriverName": "sqlite" | "mysql" | "mssql", "ConnectionString": "..." }
/// </code>
/// SQLite is the default and needs no setup. "mysql" covers both MySQL and MariaDB.
/// </summary>
public sealed record DbSettings(DbDriver Driver, string ConnectionString)
{
    public const string DefaultSqliteConnectionString = "Data Source=solpnl.db";

    /// <summary>Driver name as used in config and in migration script extensions (sqlite, mysql, mssql).</summary>
    public string DriverName => Driver switch
    {
        DbDriver.Sqlite => "sqlite",
        DbDriver.MySql => "mysql",
        DbDriver.SqlServer => "mssql",
        _ => throw new ArgumentOutOfRangeException(nameof(Driver)),
    };

    /// <summary>True once storage has been chosen (Db:DriverName is set); until then SolPnl runs the setup wizard.</summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Db:DriverName"]);

    public static DbDriver ParseDriver(string driverName) => driverName.Trim().ToLowerInvariant() switch
    {
        "sqlite" => DbDriver.Sqlite,
        "mysql" or "mariadb" => DbDriver.MySql,
        "mssql" => DbDriver.SqlServer,
        _ => throw new InvalidOperationException(
            $"Unsupported storage driver '{driverName}'. Use sqlite, mysql (MySQL/MariaDB) or mssql."),
    };

    public static DbSettings FromConfiguration(IConfiguration configuration)
    {
        var driverName = (configuration["Db:DriverName"] ?? "sqlite").Trim().ToLowerInvariant();
        var driver = ParseDriver(driverName);

        var connectionString = configuration["Db:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = driver == DbDriver.Sqlite
                ? DefaultSqliteConnectionString
                : throw new InvalidOperationException($"Db:ConnectionString is required for Db:DriverName '{driverName}'.");
        }

        return new DbSettings(driver, connectionString);
    }

    /// <summary>
    /// MySQL/MariaDB connections talk utf8mb4 unless the connection string says otherwise. Without it the connector
    /// uses the server's default character set, which on some MariaDB servers is still latin1 ("✓" arrives as "?").
    /// </summary>
    public static string WithUnicode(string mySqlConnectionString)
    {
        var builder = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(mySqlConnectionString);
        if (string.IsNullOrEmpty(builder.CharacterSet)) builder.CharacterSet = "utf8mb4";
        return builder.ConnectionString;
    }

    public void Configure(DbContextOptionsBuilder builder)
    {
        switch (Driver)
        {
            case DbDriver.Sqlite: builder.UseSqlite(ConnectionString); break;
            case DbDriver.MySql: builder.UseMySQL(WithUnicode(ConnectionString)); break;
            case DbDriver.SqlServer: builder.UseSqlServer(ConnectionString); break;
        }
    }
}

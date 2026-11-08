using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace SolPnl.Core.Data;

/// <summary>A readable name and version for the configured database, e.g. "MariaDB 11.4.5", for the About page.</summary>
public static class DatabaseDescription
{
    public static async Task<string> DescribeAsync(IDbContextFactory<SolPnlDb> factory, DbSettings settings, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            DbConnection connection = db.Database.GetDbConnection();
            await connection.OpenAsync(ct);
            return Describe(settings.Driver, connection.ServerVersion);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            return Describe(settings.Driver, null);
        }
    }

    /// <summary>Formats a provider's ServerVersion string; just the product name when the version is unknown.</summary>
    public static string Describe(DbDriver driver, string? serverVersion)
    {
        var version = serverVersion?.Trim();
        if (string.IsNullOrEmpty(version))
        {
            return driver switch
            {
                DbDriver.Sqlite => "SQLite",
                DbDriver.MySql => "MySQL / MariaDB",
                _ => "SQL Server",
            };
        }

        switch (driver)
        {
            case DbDriver.Sqlite:
                return $"SQLite {version}";

            case DbDriver.MySql:
                var mariaDb = version.IndexOf("MariaDB", StringComparison.OrdinalIgnoreCase);
                if (mariaDb < 0) return $"MySQL {version.Split('-')[0]}";
                // e.g. "11.4.5-MariaDB-ubu2404", or "5.5.5-10.11.6-MariaDB" (older servers add a compatibility prefix).
                var number = version[..mariaDb].TrimEnd('-');
                if (number.StartsWith("5.5.5-", StringComparison.Ordinal)) number = number[6..];
                return $"MariaDB {number}";

            default:
                // "16.00.4135" → "SQL Server 2022 (16.0.4135)"
                var parts = version.Split('.');
                var release = int.TryParse(parts[0], out var major) ? major switch
                {
                    17 => "2025",
                    16 => "2022",
                    15 => "2019",
                    14 => "2017",
                    13 => "2016",
                    _ => null,
                } : null;
                var normalized = string.Join('.', parts.Select(p => int.TryParse(p, out var n) ? n.ToString() : p));
                return release is null ? $"SQL Server {normalized}" : $"SQL Server {release} ({normalized})";
        }
    }
}

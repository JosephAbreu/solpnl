using Microsoft.Extensions.Configuration;
using SolPnl.Core.Data;

namespace SolPnl.Core.Tests;

public class DbSettingsTests
{
    private static DbSettings From(params (string Key, string? Value)[] values) =>
        DbSettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build());

    [Fact]
    public void Defaults_ToSqlite()
    {
        var settings = From();
        Assert.Equal(DbDriver.Sqlite, settings.Driver);
        Assert.Equal(DbSettings.DefaultSqliteConnectionString, settings.ConnectionString);
    }

    [Theory]
    [InlineData("sqlite", DbDriver.Sqlite, "sqlite")]
    [InlineData("MySQL", DbDriver.MySql, "mysql")]
    [InlineData("mariadb", DbDriver.MySql, "mysql")]
    [InlineData("mssql", DbDriver.SqlServer, "mssql")]
    public void ParsesDriverName(string configured, DbDriver expected, string scriptExtension)
    {
        var settings = From(("Db:DriverName", configured), ("Db:ConnectionString", "x"));
        Assert.Equal(expected, settings.Driver);
        Assert.Equal(scriptExtension, settings.DriverName);
    }

    [Fact]
    public void UnknownDriver_Throws() =>
        Assert.Throws<InvalidOperationException>(() => From(("Db:DriverName", "postgres")));

    [Fact]
    public void ServerDriver_WithoutConnectionString_Throws() =>
        Assert.Throws<InvalidOperationException>(() => From(("Db:DriverName", "mysql")));
}

public class DatabaseDescriptionTests
{
    [Theory]
    [InlineData(DbDriver.Sqlite, "3.46.1", "SQLite 3.46.1")]
    [InlineData(DbDriver.MySql, "8.4.3", "MySQL 8.4.3")]
    [InlineData(DbDriver.MySql, "11.4.5-MariaDB-ubu2404", "MariaDB 11.4.5")]
    [InlineData(DbDriver.MySql, "5.5.5-10.11.6-MariaDB-0+deb12u1", "MariaDB 10.11.6")]
    [InlineData(DbDriver.SqlServer, "16.00.4135", "SQL Server 2022 (16.0.4135)")]
    [InlineData(DbDriver.SqlServer, "99.00.1", "SQL Server 99.0.1")]
    [InlineData(DbDriver.MySql, null, "MySQL / MariaDB")]
    [InlineData(DbDriver.Sqlite, "", "SQLite")]
    public void Describe_FormatsServerVersion(DbDriver driver, string? serverVersion, string expected) =>
        Assert.Equal(expected, DatabaseDescription.Describe(driver, serverVersion));
}

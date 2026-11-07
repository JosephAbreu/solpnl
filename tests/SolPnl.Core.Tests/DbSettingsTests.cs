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

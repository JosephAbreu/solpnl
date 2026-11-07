using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using SolPnl.Contracts.Models;
using SolPnl.Core.Data;
using SolPnl.Core.Setup;

namespace SolPnl.Core.Tests;

public class SetupTests
{
    private static StorageSetupRequest Request(string driver) => new() { Code = "x", Driver = driver };

    [Fact]
    public void BuildSettings_Sqlite_UsesPath()
    {
        var settings = StorageSetupService.BuildSettings(Request("sqlite") with { SqlitePath = "/var/lib/solpnl/solpnl.db" });
        Assert.Equal(DbDriver.Sqlite, settings.Driver);
        Assert.Contains("/var/lib/solpnl/solpnl.db", settings.ConnectionString);
    }

    [Fact]
    public void BuildSettings_MySql_FromFields()
    {
        var settings = StorageSetupService.BuildSettings(Request("mysql") with
        {
            Server = "192.168.1.20", Port = 3307, Database = "solpnl", User = "solpnl", Password = "p;w=d",
        });
        Assert.Equal(DbDriver.MySql, settings.Driver);
        var cs = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(settings.ConnectionString);
        Assert.Equal("192.168.1.20", cs.Server);
        Assert.Equal(3307u, cs.Port);
        Assert.Equal("p;w=d", cs.Password); // special characters survive quoting
    }

    [Fact]
    public void BuildSettings_SqlServer_FromFields()
    {
        var settings = StorageSetupService.BuildSettings(Request("mssql") with
        {
            Server = "sqlbox", Port = 1444, Database = "solpnl", User = "sa", Password = "x", TrustServerCertificate = true,
        });
        var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.ConnectionString);
        Assert.Equal("sqlbox,1444", cs.DataSource);
        Assert.True(cs.TrustServerCertificate);
    }

    [Fact]
    public void BuildSettings_ConnectionString_TakesPrecedence()
    {
        var settings = StorageSetupService.BuildSettings(Request("mariadb") with { Server = "ignored", ConnectionString = " Server=a;Database=b; " });
        Assert.Equal(DbDriver.MySql, settings.Driver);
        Assert.Equal("Server=a;Database=b;", settings.ConnectionString);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mssql")]
    public void BuildSettings_MissingServer_Throws(string driver) =>
        Assert.Throws<ArgumentException>(() => StorageSetupService.BuildSettings(Request(driver) with { Database = "d", User = "u" }));

    [Fact]
    public void BuildSettings_UnknownDriver_Throws() =>
        Assert.Throws<ArgumentException>(() => StorageSetupService.BuildSettings(Request("postgres")));

    [Fact]
    public void SetupCode_VerifiesLooselyFormatted_AndLocksOut()
    {
        var state = new SetupState("7F3K-92QD");
        Assert.True(state.Verify("7f3k 92qd"));

        var other = new SetupState("7F3K-92QD");
        for (var i = 0; i < SetupState.MaxFailedAttempts; i++) Assert.False(other.Verify("WRONG-CODE"));
        Assert.True(other.IsLockedOut);
        Assert.False(other.Verify("7F3K-92QD")); // even the right code is refused once locked out
    }

    [Fact]
    public void SetupCode_IsGeneratedInExpectedFormat()
    {
        var code = new SetupState().Code;
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        Assert.NotEqual(code, new SetupState().Code);
    }

    [Fact]
    public async Task LocalSettingsFile_ReplacesSection_KeepsOthers()
    {
        var dir = Directory.CreateTempSubdirectory("solpnl-settings-");
        try
        {
            var path = Path.Combine(dir.FullName, LocalSettingsFile.FileName);
            await File.WriteAllTextAsync(path, """
                {
                  // existing machine settings
                  "Plugins": { "SolarmanSrne": { "Host": "192.168.1.50", "LoggerSerial": 2712345678 } },
                  "Db": { "DriverName": "old" },
                }
                """);

            await new LocalSettingsFile(path).SetSectionAsync("Db", new JsonObject { ["DriverName"] = "sqlite", ["ConnectionString"] = "Data Source=x.db" });

            var config = new ConfigurationBuilder().AddJsonFile(path).Build();
            Assert.Equal("192.168.1.50", config["Plugins:SolarmanSrne:Host"]);
            Assert.Equal("sqlite", config["Db:DriverName"]);
            Assert.Equal("Data Source=x.db", config["Db:ConnectionString"]);
            Assert.True(DbSettings.IsConfigured(config));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LocalSettingsFile_CreatesFileWhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory("solpnl-settings-");
        try
        {
            var path = Path.Combine(dir.FullName, "nested", LocalSettingsFile.FileName);
            await new LocalSettingsFile(path).SetSectionAsync("Db", new JsonObject { ["DriverName"] = "sqlite" });
            Assert.Equal("sqlite", new ConfigurationBuilder().AddJsonFile(path).Build()["Db:DriverName"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Test_ReportsConnectionErrors()
    {
        var result = await StorageSetupService.TestAsync(new DbSettings(DbDriver.MySql, "Server=127.0.0.1;Port=1;Database=x;User=x;Password=x;Connection Timeout=2"));
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }
}

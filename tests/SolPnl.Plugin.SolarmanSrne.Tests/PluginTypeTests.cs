namespace SolPnl.Plugin.SolarmanSrne.Tests;

public class PluginTypeTests
{
    private static Dictionary<string, string?> Settings(string? host = "192.168.1.50", string? serial = "2712345678",
        string? port = null, string? slave = null) =>
        new() { ["host"] = host, ["loggerSerial"] = serial, ["port"] = port, ["slaveId"] = slave };

    [Fact]
    public void ParseOptions_UsesDefaults()
    {
        var options = SolarmanSrnePluginType.ParseOptions(Settings());
        Assert.Equal("192.168.1.50", options.Host);
        Assert.Equal(2712345678u, options.LoggerSerial);
        Assert.Equal(8899, options.Port);
        Assert.Equal((byte)1, options.SlaveId);
    }

    [Fact]
    public void ParseOptions_AcceptsHostNamesAndOverrides()
    {
        var options = SolarmanSrnePluginType.ParseOptions(Settings(host: " logger.local ", port: "8900", slave: "2"));
        Assert.Equal("logger.local", options.Host);
        Assert.Equal(8900, options.Port);
        Assert.Equal((byte)2, options.SlaveId);
    }

    [Theory]
    [InlineData(null, "2712345678", null, null, "Logger address is required")]
    [InlineData("bad host", "2712345678", null, null, "isn't a valid")]
    [InlineData("192.168.1.50", "abc", null, null, "serial number")]
    [InlineData("192.168.1.50", "0", null, null, "serial number")]
    [InlineData("192.168.1.50", "99999999999", null, null, "serial number")]
    [InlineData("192.168.1.50", "2712345678", "70000", null, "Port")]
    [InlineData("192.168.1.50", "2712345678", null, "300", "Modbus address")]
    public void ParseOptions_RejectsInvalid(string? host, string? serial, string? port, string? slave, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => SolarmanSrnePluginType.ParseOptions(Settings(host, serial, port, slave)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Create_DeviceIdComesFromLoggerSerial()
    {
        var type = new SolarmanSrnePluginType();
        await using var plugin = (SolarmanSrnePlugin)type.Create(Settings());
        Assert.Equal("solarman-2712345678", (await plugin.GetDeviceInfoAsync()).DeviceId);
    }

    [Fact]
    public void Settings_DescribeRequiredAndOptionalFields()
    {
        var fields = new SolarmanSrnePluginType().Settings.ToDictionary(f => f.Key);
        Assert.True(fields["host"].Required);
        Assert.True(fields["loggerSerial"].Required);
        Assert.False(fields["port"].Required);
        Assert.Equal("8899", fields["port"].Default);
    }
}

namespace SolPnl.Plugin.SolarmanDeye.Tests;

public class PluginTypeTests
{
    private static Dictionary<string, string?> Settings(string? host = "192.168.1.50", string? serial = "2712345678",
        string? phases = null, string? port = null, string? slave = null) =>
        new() { ["host"] = host, ["loggerSerial"] = serial, ["phases"] = phases, ["port"] = port, ["slaveId"] = slave };

    [Fact]
    public void ParseOptions_UsesDefaults()
    {
        var options = SolarmanDeyePluginType.ParseOptions(Settings());
        Assert.Equal("192.168.1.50", options.Host);
        Assert.Equal(2712345678u, options.LoggerSerial);
        Assert.False(options.SplitPhase);
        Assert.Equal(8899, options.Port);
        Assert.Equal((byte)1, options.SlaveId);
    }

    [Fact]
    public void ParseOptions_SplitPhase()
    {
        var options = SolarmanDeyePluginType.ParseOptions(Settings(phases: SolarmanDeyePluginType.SplitPhase));
        Assert.True(options.SplitPhase);
    }

    [Fact]
    public void Settings_PhaseChoiceOffersBothOptions()
    {
        var field = new SolarmanDeyePluginType().Settings.Single(s => s.Key == "phases");
        Assert.Equal([SolarmanDeyePluginType.SinglePhase, SolarmanDeyePluginType.SplitPhase], field.Options);
        Assert.Equal(SolarmanDeyePluginType.SinglePhase, field.Default);
    }

    [Theory]
    [InlineData(null, "2712345678", null, "Logger address is required")]
    [InlineData("bad host", "2712345678", null, "isn't a valid")]
    [InlineData("192.168.1.50", "0", null, "serial number")]
    [InlineData("192.168.1.50", "2712345678", "Three phase", "Grid connection")]
    public void ParseOptions_RejectsInvalid(string? host, string? serial, string? phases, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => SolarmanDeyePluginType.ParseOptions(Settings(host, serial, phases)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task DeviceInfo_NeedsNoConnection()
    {
        // Nothing listens on this port; GetDeviceInfoAsync must still answer (adding a device works offline).
        await using var plugin = new SolarmanDeyePlugin(SolarmanDeyePluginType.ParseOptions(Settings(host: "127.0.0.1", port: "1")));
        var info = await plugin.GetDeviceInfoAsync();
        Assert.Equal("deye-2712345678", info.DeviceId);
    }
}

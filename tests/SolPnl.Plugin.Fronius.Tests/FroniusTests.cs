using System.Net;
using System.Text;
using System.Text.Json;
using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.Fronius.Tests;

/// <summary>
/// Tests against responses shaped like pyfronius' captured ones (tests/test_structure/v1/solar_api/v1). The night-time
/// power flow below copies its signs: P_Grid +367.722145, P_Load -367.722145, P_PV and P_Akku null.
/// </summary>
public class FroniusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    private static string Ok(string data) =>
        $$$"""{"Head":{"Status":{"Code":0,"Reason":"","UserMessage":""},"Timestamp":"2026-11-09T18:00:00+00:00"},"Body":{"Data":{{{data}}}}}""";

    private static string Status(int code, string reason) =>
        "{\"Head\":{\"Status\":{\"Code\":" + code + ",\"Reason\":\"" + reason + "\"}},\"Body\":{\"Data\":{}}}";

    private const string ApiVersion = """{"APIVersion":1,"BaseURL":"/solar_api/v1/","CompatibilityRange":"1.8-1"}""";

    private static readonly string NightFlow = Ok("""
        {"Site":{"Mode":"vague-meter","Meter_Location":"load","P_Grid":367.722145,"P_Load":-367.722145,"P_Akku":null,
         "P_PV":null,"E_Day":0,"E_Total":26213502},"Inverters":{"1":{"DT":123,"P":null}}}
        """);

    private static readonly string DayFlow = Ok("""
        {"Site":{"Mode":"bidirectional","P_Grid":-1820.5,"P_Load":-2400.25,"P_Akku":-1100,"P_PV":5320.75},
         "Inverters":{"1":{"DT":1,"P":4220.75,"SOC":63.5}}}
        """);

    private static readonly string Common = Ok("""
        {"PAC":{"Value":4220.75,"Unit":"W"},"FAC":{"Value":50.02,"Unit":"Hz"},"UAC":{"Value":231.4,"Unit":"V"},
         "IAC":{"Value":6.1,"Unit":"A"},"UDC":{"Value":612.3,"Unit":"V"},"IDC":{"Value":4.6,"Unit":"A"},
         "UDC_2":{"Value":598.1,"Unit":"V"},"IDC_2":{"Value":4.2,"Unit":"A"},
         "DAY_ENERGY":{"Value":18340,"Unit":"Wh"},"TOTAL_ENERGY":{"Value":26213502,"Unit":"Wh"},
         "DeviceStatus":{"StatusCode":7,"ErrorCode":0,"LEDColor":2,"LEDState":0}}
        """);

    private static readonly string ThreePhase = Ok("""
        {"IAC_L1":{"Value":0.92972046136856079,"Unit":"A"},"IAC_L2":{"Value":0.95,"Unit":"A"},"IAC_L3":{"Value":0.91,"Unit":"A"},
         "UAC_L1":{"Value":231.87258911132812,"Unit":"V"},"UAC_L2":{"Value":232.1,"Unit":"V"},"UAC_L3":{"Value":230.6,"Unit":"V"}}
        """);

    private static readonly string Meter = Ok("""
        {"0":{"Enable":1,"Visible":1,"Meter_Location_Current":0,"PowerReal_P_Sum":-1820.5,
         "Voltage_AC_Phase_1":232.4,"Voltage_AC_Phase_2":231.9,"Voltage_AC_Phase_3":233.0,
         "Current_AC_Phase_1":3.1,"Current_AC_Phase_2":2.6,"Current_AC_Phase_3":2.2,"Frequency_Phase_Average":50.0,
         "EnergyReal_WAC_Sum_Consumed":4567000,"EnergyReal_WAC_Sum_Produced":12345000}}
        """);

    private static readonly string Storage = Ok("""
        {"0":{"Controller":{"Capacity_Maximum":11520,"DesignedCapacity":11520,"StateOfCharge_Relative":7.9,
         "Temperature_Cell":19.55,"Voltage_DC":405.2,"Enable":1,"Details":{"Manufacturer":"BYD"}},"Modules":[]}}
        """);

    /// <summary>Answers by path + query; anything else is a 404.</summary>
    private sealed class FakeFronius(Dictionary<string, string> routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            Requests.Add(key);
            return Task.FromResult(routes.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string Inv = "/solar_api/v1/GetInverterRealtimeData.cgi?Scope=Device&DeviceId=1&DataCollection=";

    private static Dictionary<string, string> Routes(bool night = false, bool meter = true, bool storage = true, bool threePhase = true)
    {
        var r = new Dictionary<string, string>
        {
            ["/solar_api/GetAPIVersion.cgi"] = ApiVersion,
            ["/solar_api/v1/GetPowerFlowRealtimeData.fcgi"] = night ? NightFlow : DayFlow,
            [Inv + "CommonInverterData"] = night ? Status(12, "Device not available") : Common,
            [Inv + "3PInverterData"] = night ? Status(12, "Device not available") : threePhase ? ThreePhase : Ok("{}"),
            ["/solar_api/v1/GetStorageRealtimeData.cgi?Scope=System"] = storage ? Storage : Status(255, "Storages are not supported"),
        };
        if (meter) r["/solar_api/v1/GetMeterRealtimeData.cgi?Scope=System"] = Meter;
        return r;
    }

    private static async Task<(Dictionary<string, Reading> Readings, FakeFronius Fake, FroniusPlugin Plugin)> ReadAsync(
        Dictionary<string, string> routes, FroniusPlugin? plugin = null, FakeFronius? fake = null)
    {
        fake ??= new FakeFronius(routes);
        plugin ??= new FroniusPluginType(new HttpClient(fake)).Create(new Dictionary<string, string?> { ["host"] = "192.168.1.60" }) as FroniusPlugin;
        var readings = (await plugin!.ReadAsync()).ToDictionary(r => r.Metric);
        return (readings, fake, plugin);
    }

    [Theory]
    [InlineData("pv.power", 5320.75, "W")]
    [InlineData("grid.power", -1820.5, "W")]               // exporting
    [InlineData("load.power", 2400.25, "W")]
    [InlineData("battery.power", 1100, "W")]                // P_Akku -1100: charging
    [InlineData("battery.soc", 63.5, "%")]                  // from the power flow, ahead of the storage's 7.9
    [InlineData("battery.voltage", 405.2, "V")]
    [InlineData("battery.current", 2.715, "A")]
    [InlineData("vendor.fronius.battery_temperature", 19.55, "°C")]
    [InlineData("vendor.fronius.ac_power", 4220.75, "W")]
    [InlineData("inverter.voltage.l1", 231.873, "V")]
    [InlineData("inverter.voltage.l3", 230.6, "V")]
    [InlineData("inverter.current.l2", 0.95, "A")]
    [InlineData("inverter.frequency", 50.02, "Hz")]
    [InlineData("pv.voltage.1", 612.3, "V")]
    [InlineData("pv.power.1", 2816.58, "W")]
    [InlineData("pv.current.2", 4.2, "A")]
    [InlineData("vendor.fronius.energy.pv_today", 18.34, "kWh")]
    [InlineData("vendor.fronius.energy.pv_total", 26213.502, "kWh")]
    [InlineData("vendor.fronius.status", 7, "")]
    [InlineData("fault.code.1", 0, "")]
    [InlineData("grid.voltage.l1", 232.4, "V")]
    [InlineData("grid.voltage.l3", 233.0, "V")]
    [InlineData("grid.current.l2", 2.6, "A")]
    [InlineData("grid.frequency", 50.0, "Hz")]
    [InlineData("vendor.fronius.energy.grid_import_total", 4567, "kWh")]
    [InlineData("vendor.fronius.energy.grid_export_total", 12345, "kWh")]
    public async Task Day_FullSystem(string metric, double expected, string unit)
    {
        var (readings, _, _) = await ReadAsync(Routes());
        Assert.Equal(expected, readings[metric].Value, 3);
        Assert.Equal(unit, readings[metric].Unit);
    }

    [Fact]
    public async Task Night_InverterAsleep_StillReportsFlowAndGrid()
    {
        var (readings, _, _) = await ReadAsync(Routes(night: true));
        Assert.Equal(0, readings["pv.power"].Value);       // P_PV null
        Assert.Equal(367.722, readings["grid.power"].Value, 3);
        Assert.Equal(367.722, readings["load.power"].Value, 3);
        Assert.Equal(232.4, readings["grid.voltage.l1"].Value, 3); // from the meter: no false outage
        Assert.DoesNotContain("battery.power", readings.Keys); // P_Akku null
        Assert.DoesNotContain(readings.Keys, m => m.StartsWith("inverter.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoMeterOrStorage_AreDroppedAfterTheFirstRead()
    {
        var (readings, fake, plugin) = await ReadAsync(Routes(meter: false, storage: false));
        Assert.DoesNotContain(readings.Keys, m => m.StartsWith("grid.voltage", StringComparison.Ordinal));
        Assert.DoesNotContain("battery.voltage", readings.Keys);

        fake.Requests.Clear();
        await ReadAsync(Routes(), plugin, fake);
        Assert.DoesNotContain(fake.Requests, r => r.Contains("Meter") || r.Contains("Storage") || r.Contains("APIVersion"));
    }

    [Fact]
    public async Task SinglePhaseInverter_UsesCommonValues()
    {
        var (readings, fake, plugin) = await ReadAsync(Routes(threePhase: false));
        Assert.Equal(231.4, readings["inverter.voltage.l1"].Value, 3);
        Assert.DoesNotContain("inverter.voltage.l2", readings.Keys);

        fake.Requests.Clear();
        await ReadAsync(Routes(threePhase: false), plugin, fake);
        Assert.DoesNotContain(fake.Requests, r => r.Contains("3PInverterData")); // learned
    }

    [Fact]
    public async Task ErrorCode_IsAFaultOnlyInTheErrorState()
    {
        var routes = Routes();
        routes[Inv + "CommonInverterData"] = Common.Replace("\"StatusCode\":7,\"ErrorCode\":0", "\"StatusCode\":8,\"ErrorCode\":306");
        Assert.Equal(0, (await ReadAsync(routes)).Readings["fault.code.1"].Value); // "power low" at dusk: not a fault

        routes[Inv + "CommonInverterData"] = Common.Replace("\"StatusCode\":7,\"ErrorCode\":0", "\"StatusCode\":10,\"ErrorCode\":102");
        var readings = (await ReadAsync(routes)).Readings;
        Assert.Equal(102, readings["fault.code.1"].Value);
        Assert.Equal("Error", FroniusStates.Describe((int)readings["vendor.fronius.status"].Value));
    }

    [Fact]
    public async Task ApiVersion0_ExplainsTheFirmwareUpdate()
    {
        var routes = Routes();
        routes["/solar_api/GetAPIVersion.cgi"] = """{"APIVersion":0,"BaseURL":"/solar_api/"}""";
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(routes));
        Assert.Contains("version 0", ex.Message);
    }

    [Fact]
    public async Task PowerFlowError_FailsThePoll()
    {
        var routes = Routes();
        routes["/solar_api/v1/GetPowerFlowRealtimeData.fcgi"] = Status(255, "UnknownError");
        await Assert.ThrowsAsync<FroniusDecoder.StatusException>(() => ReadAsync(routes));
    }

    [Theory]
    [InlineData("192.168.1.60", "http://192.168.1.60/", 1)]
    [InlineData("http://inverter.local:8080/status", "http://inverter.local:8080/", 1)]
    [InlineData("10.0.0.2", "http://10.0.0.2/", 2, "2")]
    public void ParseOptions_AcceptsAddresses(string host, string expected, int device, string? deviceId = null)
    {
        var o = FroniusPluginType.ParseOptions(new Dictionary<string, string?> { ["host"] = host, ["deviceId"] = deviceId });
        Assert.Equal((expected, device), (o.BaseUri.ToString(), o.DeviceId));
    }

    [Theory]
    [InlineData(null, null, "Inverter address is required")]
    [InlineData("bad host", null, "isn't a valid")]
    [InlineData("ftp://x", null, "isn't a valid")]
    [InlineData("10.0.0.2", "300", "Inverter number")]
    public void ParseOptions_RejectsInvalid(string? host, string? deviceId, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            FroniusPluginType.ParseOptions(new Dictionary<string, string?> { ["host"] = host, ["deviceId"] = deviceId }));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void DeviceKey_FromSettings()
    {
        Assert.Equal("fronius-inverter-local-8080-1", FroniusPlugin.DeviceKey(new Uri("http://Inverter.local:8080/"), 1));
    }

    [Fact]
    public void Data_ThrowsOnBadStatus()
    {
        using var doc = JsonDocument.Parse(Status(12, "Device not available"));
        var ex = Assert.Throws<FroniusDecoder.StatusException>(() => FroniusDecoder.Data(doc));
        Assert.Equal(12, ex.Code);
    }
}

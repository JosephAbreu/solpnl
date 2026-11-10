using SolPnl.Contracts.Models;
using SolPnl.Modbus;

namespace SolPnl.Plugin.VictronGx.Tests;

/// <summary>
/// Decoding against hand-built register sets laid out as in Victron's register definitions (no live capture yet).
/// Values are chosen so a wrong address, scale or sign shows up as a wrong number.
/// </summary>
public class VictronTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    private static ushort S(int v) => unchecked((ushort)(short)v);

    /// <summary>Unit 100: two phases in use, 3 kW of AC PV, 1.2 kW DC PV, importing 400 W, battery charging.</summary>
    internal static Dictionary<ushort, ushort> System()
    {
        var r = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in VictronRegisterMap.SystemBlocks)
            for (var i = 0; i < count; i++) r[(ushort)(start + i)] = 0;
        r[808] = 1800; r[809] = 1200;                 // AC PV on output L1, L2
        r[817] = 1500; r[818] = 1100;                 // consumption L1, L2
        r[820] = 250; r[821] = 150;                   // grid L1, L2 (+ import)
        r[826] = 1;                                   // active input: grid
        r[840] = 528; r[841] = S(-123); r[842] = S(-650); r[843] = 76; r[844] = 2; // discharging
        r[850] = 1200;
        r[860] = S(-35);
        return r;
    }

    /// <summary>VE.Bus split-phase Multi on AC input 1, inverting state, no error.</summary>
    internal static Dictionary<ushort, ushort> Vebus()
    {
        var r = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in VictronRegisterMap.VebusBlocks)
            for (var i = 0; i < count; i++) r[(ushort)(start + i)] = 0;
        r[3] = 1203; r[4] = 1198;                     // AC in V
        r[6] = 21; r[7] = S(-4);                      // AC in A
        r[9] = 5998;                                  // AC in Hz
        r[12] = 25; r[13] = 15;                       // AC in power, in tens of W
        r[15] = 1201; r[16] = 1199;                   // AC out V
        r[18] = 125; r[19] = 92;
        r[21] = 6000;
        r[28] = 2;                                    // phases
        r[29] = 0;
        r[31] = 9;                                    // inverting
        r[32] = 0;
        return r;
    }

    /// <summary>A SmartSolar charger: 98.5 V PV, 812.0 W, 3.4 kWh today.</summary>
    internal static Dictionary<ushort, ushort> Charger(ushort error = 0)
    {
        var r = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in VictronRegisterMap.SolarChargerBlocks)
            for (var i = 0; i < count; i++) r[(ushort)(start + i)] = 0;
        r[775] = 3;                                   // bulk
        r[776] = 9850;
        r[784] = 34;
        r[788] = error;
        r[789] = 8120;
        return r;
    }

    private static Dictionary<string, Reading> Decode(
        Dictionary<ushort, ushort> system, Dictionary<ushort, ushort>? vebus = null, params Dictionary<ushort, ushort>[] chargers) =>
        VictronRegisterMap.Decode(new VictronRegisterMap.Inputs(system, vebus, chargers), "dev", Now).ToDictionary(r => r.Metric);

    [Theory]
    [InlineData("pv.power", 4200, "W")]                       // 3000 AC + 1200 DC
    [InlineData("vendor.victron.ac_pv_power", 3000, "W")]
    [InlineData("vendor.victron.dc_pv_power", 1200, "W")]
    [InlineData("load.power", 2600, "W")]
    [InlineData("load.power.l2", 1100, "W")]
    [InlineData("grid.power", 400, "W")]
    [InlineData("grid.power.l1", 250, "W")]
    [InlineData("battery.voltage", 52.8, "V")]
    [InlineData("battery.current", -12.3, "A")]
    [InlineData("battery.power", -650, "W")]
    [InlineData("battery.soc", 76, "%")]
    [InlineData("vendor.victron.active_input", 1, "")]
    [InlineData("vendor.victron.dc_system_power", -35, "W")]
    [InlineData("grid.voltage.l1", 120.3, "V")]
    [InlineData("grid.voltage.l2", 119.8, "V")]
    [InlineData("grid.current.l1", 2.1, "A")]
    [InlineData("grid.current.l2", -0.4, "A")]
    [InlineData("grid.frequency", 59.98, "Hz")]
    [InlineData("inverter.voltage.l1", 120.1, "V")]
    [InlineData("inverter.current.l1", 12.5, "A")]
    [InlineData("inverter.frequency", 60.0, "Hz")]
    [InlineData("vendor.victron.vebus_state", 9, "")]
    [InlineData("pv.voltage.1", 98.5, "V")]
    [InlineData("pv.power.1", 812.0, "W")]
    [InlineData("pv.current.1", 8.244, "A")]                 // derived: 812 W ÷ 98.5 V
    [InlineData("vendor.victron.charger_yield_today.1", 3.4, "kWh")]
    [InlineData("fault.code.1", 0, "")]
    public void Decode_FullSystem(string metric, double expected, string unit)
    {
        var reading = Decode(System(), Vebus(), Charger())[metric];
        Assert.Equal(expected, reading.Value, 3);
        Assert.Equal(unit, reading.Unit);
    }

    [Fact]
    public void Decode_SystemOnly_SinglePhaseNoGridVoltage()
    {
        // Without a VE.Bus device: phases default to one, and there's no AC input voltage (so no outage detection).
        var readings = Decode(System());
        Assert.Equal(1500, readings["load.power"].Value);
        Assert.Equal(1800 + 1200, readings["pv.power"].Value);
        Assert.DoesNotContain(readings.Keys, m => m.EndsWith(".l2", StringComparison.Ordinal));
        Assert.DoesNotContain(readings.Keys, m => m.StartsWith("grid.voltage", StringComparison.Ordinal));
        Assert.DoesNotContain(readings.Keys, m => m.StartsWith("fault.", StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_NoBattery_SkipsBattery()
    {
        var system = System();
        system[840] = 0;
        Assert.DoesNotContain(Decode(system).Keys, m => m.StartsWith("battery.", StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_MissingBlocks_AreSkipped()
    {
        var system = System();
        system.Remove(850); // DC PV refused by the GX: no DC solar chargers
        var readings = Decode(system);
        Assert.Equal(1800, readings["pv.power"].Value); // single phase without VE.Bus: L1 AC PV only
        Assert.DoesNotContain("vendor.victron.dc_pv_power", readings.Keys);
    }

    [Fact]
    public void Decode_GridLost_ReadsZeroInputVoltage()
    {
        var vebus = Vebus();
        vebus[3] = 0; vebus[4] = 0; vebus[29] = 240; // input disconnected: the outage monitor sees 0 V
        var readings = Decode(System(), vebus);
        Assert.Equal(0, readings["grid.voltage.l1"].Value);
        Assert.Equal(0, readings["grid.voltage.l2"].Value);
    }

    [Fact]
    public void Decode_ErrorsAlarmsAndChargerErrors_BecomeFaultCodes()
    {
        var vebus = Vebus();
        vebus[32] = 26;      // internal error
        vebus[35] = 2;       // low battery alarm (1 = warning, not reported)
        vebus[36] = 1;
        var readings = Decode(System(), vebus, Charger(), Charger(error: 17));
        Assert.Equal(26, readings["fault.code.1"].Value);
        Assert.Equal(102, readings["fault.code.2"].Value);
        Assert.Equal(317, readings["fault.code.3"].Value);
        Assert.Equal(0, readings["fault.code.4"].Value);
        Assert.Equal("Internal error", VictronCodes.Describe(26));
        Assert.Equal("Battery low", VictronCodes.Describe(102));
        Assert.Equal("Solar charger: Charger too hot", VictronCodes.Describe(317));
        Assert.Null(VictronCodes.Describe(399));
    }

    [Fact]
    public void States_AreNamed()
    {
        Assert.Equal("Inverting", VictronCodes.DescribeState(9));
        Assert.Equal("Charging (float)", VictronCodes.DescribeState(5));
        Assert.Null(VictronCodes.DescribeState(99));
    }

    [Fact]
    public void Blocks_AvoidReservedAndInternalRegisters()
    {
        // Reserved (827-839 gaps, 845+, 852-859) and internal (777) registers can fail a whole read.
        int[] off = [827, 830, 834, 836, 845, 848, 852, 856, 861, 777];
        var used = VictronRegisterMap.SystemBlocks.Concat(VictronRegisterMap.SolarChargerBlocks)
            .SelectMany(b => Enumerable.Range(b.Start, b.Count));
        Assert.Empty(used.Intersect(off));
    }

    [Fact]
    public void DecodeSerial_ReadsAscii()
    {
        ushort P(char a, char b) => (ushort)(a << 8 | b);
        Assert.Equal("c0619ab1f2e3", VictronRegisterMap.DecodeSerial([P('c', '0'), P('6', '1'), P('9', 'a'), P('b', '1'), P('f', '2'), P('e', '3')]));
        Assert.Null(VictronRegisterMap.DecodeSerial([0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public async Task Plugin_FindsVebusUnitAndReadsChargers()
    {
        await using var server = new FakeModbusServer(new()
        {
            [100] = System(),
            [228] = Vebus(),   // a MultiPlus-II GX: not the first candidate
            [229] = Charger(),
        });
        await using var plugin = new VictronGxPlugin(VictronGxPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = server.Port.ToString(), ["solarChargerUnitIds"] = "229",
        }));

        var readings = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        Assert.Equal(120.3, readings["grid.voltage.l1"].Value, 3);
        Assert.Equal(812, readings["pv.power.1"].Value, 3);
        Assert.Equal(400, readings["grid.power"].Value);
        Assert.Equal("victron-127-0-0-1", readings["grid.power"].DeviceId);

        // The VE.Bus unit is remembered: the second poll doesn't probe 227 again.
        var before = server.Requests;
        await plugin.ReadAsync();
        var perPoll = VictronRegisterMap.SystemBlocks.Count + VictronRegisterMap.VebusBlocks.Count + VictronRegisterMap.SolarChargerBlocks.Count;
        Assert.Equal(perPoll, server.Requests - before);
    }

    [Fact]
    public async Task Plugin_WrongChargerUnit_Fails()
    {
        await using var server = new FakeModbusServer(new() { [100] = System() });
        await using var plugin = new VictronGxPlugin(VictronGxPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = server.Port.ToString(), ["solarChargerUnitIds"] = "239",
        }));
        await Assert.ThrowsAsync<ModbusException>(() => plugin.ReadAsync());
    }

    [Theory]
    [InlineData(null, null, null, "address is required")]
    [InlineData("bad host", null, null, "isn't a valid")]
    [InlineData("10.0.0.7", "300", null, "Inverter/charger unit ID")]
    [InlineData("10.0.0.7", null, "226, x", "Solar charger unit IDs")]
    public void ParseOptions_RejectsInvalid(string? host, string? vebus, string? chargers, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => VictronGxPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = host, ["vebusUnitId"] = vebus, ["solarChargerUnitIds"] = chargers,
        }));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ParseOptions_ParsesChargerList()
    {
        var o = VictronGxPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "cerbo.local", ["solarChargerUnitIds"] = "226, 229;226",
        });
        Assert.Equal([226, 229], o.SolarChargerUnitIds);
        Assert.Null(o.VebusUnitId);
        Assert.Equal(502, o.Port);
    }
}

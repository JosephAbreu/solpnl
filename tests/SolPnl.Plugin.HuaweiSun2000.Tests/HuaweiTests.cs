using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.HuaweiSun2000.Tests;

/// <summary>
/// Decoding against hand-built register sets laid out as in huawei-solar-lib (no live capture yet). Values are chosen
/// so a wrong address, scale, sign or word order shows up as a wrong number.
/// </summary>
public class HuaweiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    private static ushort S(int v) => unchecked((ushort)(short)v);

    private static void Put32(Dictionary<ushort, ushort> r, ushort at, long value)
    {
        var v = unchecked((uint)value);
        r[at] = (ushort)(v >> 16);                    // high word first
        r[(ushort)(at + 1)] = (ushort)v;
    }

    private static void PutText(Dictionary<ushort, ushort> r, ushort at, int registers, string text)
    {
        for (var i = 0; i < registers; i++) r[(ushort)(at + i)] = 0;
        for (var i = 0; i < text.Length; i++)
            r[(ushort)(at + i / 2)] |= (ushort)(i % 2 == 0 ? text[i] << 8 : text[i]);
    }

    private static Dictionary<ushort, ushort> Zeros(IEnumerable<(ushort Start, ushort Count)> blocks, Dictionary<ushort, ushort>? into = null)
    {
        var r = into ?? new Dictionary<ushort, ushort>();
        foreach (var (start, count) in blocks)
            for (var i = 0; i < count; i++) r[(ushort)(start + i)] = 0;
        return r;
    }

    /// <summary>SUN2000-10KTL-M1 (three phase, 2 strings) with a three-phase meter and a LUNA2000 battery.</summary>
    internal static Dictionary<ushort, ushort> Registers(bool meter = true, bool battery = true)
    {
        var r = Zeros(HuaweiRegisterMap.IdentityBlocks.Concat(HuaweiRegisterMap.InverterBlocks(2)));
        PutText(r, 30000, 15, "SUN2000-10KTL-M1");
        PutText(r, 30015, 10, "HV2150012345");
        r[30071] = 2;
        Put32(r, 30073, 10_000);

        r[32089] = 0x0200;                            // on grid
        r[32016] = 6012; r[32017] = 812;              // PV1: 601.2 V, 8.12 A
        r[32018] = 5890; r[32019] = 790;
        Put32(r, 32064, 9_420);                       // input (DC) power
        r[32069] = 2301; r[32070] = 2310; r[32071] = 2295;
        Put32(r, 32072, 12_345); Put32(r, 32074, 12_400); Put32(r, 32076, 12_290); // 12.345 A
        Put32(r, 32080, 8_500);                       // AC power (includes battery)
        r[32085] = 5001;                              // 50.01 Hz
        r[32086] = 9812;                              // 98.12 %
        r[32087] = 452;                               // 45.2 °C
        Put32(r, 32114, 4_321);                       // 43.21 kWh today

        if (meter)
        {
            Zeros([HuaweiRegisterMap.MeterBlock], r);
            r[37100] = 1;                             // normal
            Put32(r, 37101, 2312); Put32(r, 37103, 2305); Put32(r, 37105, 2299);
            Put32(r, 37107, 845);                     // 8.45 A
            Put32(r, 37113, 3_200);                   // + exporting 3.2 kW
            r[37118] = 5000;
            Put32(r, 37119, -1_234_500);              // exported 12345 kWh (sign stripped)
            Put32(r, 37121, 456_700);                 // imported 4567 kWh
            r[37125] = 1;                             // three phase
            Put32(r, 37132, 1_000); Put32(r, 37134, 1_100); Put32(r, 37136, 1_100);
        }

        if (battery)
        {
            Zeros(HuaweiRegisterMap.BatteryBlocks, r);
            Put32(r, 37758, 10_000);                  // 10 kWh
            r[37760] = 873;                           // 87.3 %
            r[37762] = 2;                             // running
            r[37763] = 4512;                          // 451.2 V
            r[37764] = 42;                            // 4.2 A
            Put32(r, 37765, 1_900);                   // + charging
            Put32(r, 37784, 650);
        }
        return r;
    }

    private static Dictionary<string, Reading> Decode(Dictionary<ushort, ushort> r) =>
        HuaweiRegisterMap.Decode(r, HuaweiRegisterMap.DecodeIdentity(r), "dev", Now).ToDictionary(x => x.Metric);

    [Theory]
    [InlineData("vendor.huawei.device_status", 0x0200, "")]
    [InlineData("pv.voltage.1", 601.2, "V")]
    [InlineData("pv.current.1", 8.12, "A")]
    [InlineData("pv.power.1", 4881.744, "W")]
    [InlineData("pv.power", 9420, "W")]
    [InlineData("vendor.huawei.ac_power", 8500, "W")]
    [InlineData("inverter.voltage.l1", 230.1, "V")]
    [InlineData("inverter.voltage.l3", 229.5, "V")]
    [InlineData("inverter.current.l1", 12.345, "A")]
    [InlineData("inverter.frequency", 50.01, "Hz")]
    [InlineData("vendor.huawei.efficiency", 98.12, "%")]
    [InlineData("inverter.temperature", 45.2, "°C")]
    [InlineData("vendor.huawei.energy.pv_today", 43.21, "kWh")]
    [InlineData("grid.voltage.l1", 231.2, "V")]
    [InlineData("grid.voltage.l3", 229.9, "V")]
    [InlineData("grid.current.l1", 8.45, "A")]
    [InlineData("grid.power", -3200, "W")]                   // exporting
    [InlineData("grid.power.l2", -1100, "W")]
    [InlineData("grid.frequency", 50.0, "Hz")]
    [InlineData("vendor.huawei.energy.grid_export_total", 12345, "kWh")]
    [InlineData("vendor.huawei.energy.grid_import_total", 4567, "kWh")]
    [InlineData("load.power", 5300, "W")]                    // 8500 AC - 3200 exported
    [InlineData("battery.soc", 87.3, "%")]
    [InlineData("battery.voltage", 451.2, "V")]
    [InlineData("battery.current", 4.2, "A")]
    [InlineData("battery.power", 1900, "W")]
    [InlineData("vendor.huawei.energy.battery_charge_today", 6.5, "kWh")]
    [InlineData("fault.code.1", 0, "")]
    public void Decode_FullSystem(string metric, double expected, string unit)
    {
        var reading = Decode(Registers())[metric];
        Assert.Equal(expected, reading.Value, 3);
        Assert.Equal(unit, reading.Unit);
    }

    [Fact]
    public void Decode_WithoutMeter_NoGridOrLoad()
    {
        // The inverter's own AC voltage isn't the grid's: asleep at night it would look like an outage.
        var readings = Decode(Registers(meter: false));
        Assert.DoesNotContain(readings.Keys, m => m.StartsWith("grid.", StringComparison.Ordinal));
        Assert.DoesNotContain("load.power", readings.Keys);
        Assert.Contains("inverter.voltage.l1", readings.Keys);
    }

    [Fact]
    public void Decode_MeterOffline_IsIgnored()
    {
        var r = Registers();
        r[37100] = 0;
        Assert.DoesNotContain("grid.power", Decode(r).Keys);
    }

    [Fact]
    public void Decode_NoBattery_SkipsBattery()
    {
        var r = Registers(battery: false);
        Zeros(HuaweiRegisterMap.BatteryBlocks, r); // reads as zeros, capacity 0
        Assert.DoesNotContain(Decode(r).Keys, m => m.StartsWith("battery.", StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_SinglePhaseInverterAndMeter()
    {
        var r = Registers();
        r[32070] = 0; r[32071] = 0;
        r[37125] = 0;
        var readings = Decode(r);
        Assert.DoesNotContain(readings.Keys, m => m.EndsWith(".l2", StringComparison.Ordinal));
        Assert.Equal(-3200, readings["grid.power"].Value);
    }

    [Fact]
    public void Decode_Discharging_IsNegative()
    {
        var r = Registers();
        Put32(r, 37765, -2_500);
        Assert.Equal(-2500, Decode(r)["battery.power"].Value);
    }

    [Fact]
    public void Decode_HighWordFirst()
    {
        var r = Registers();
        Put32(r, 32064, 70_000);
        Assert.Equal(70_000, Decode(r)["pv.power"].Value);
    }

    [Fact]
    public void Decode_AlarmsBecomeHuaweiAlarmIds()
    {
        var r = Registers();
        r[32008] = 1 << 7;                            // grid loss
        r[32009] = 1 << 3;                            // overtemperature
        r[32010] = 1 << 12;                           // CT wiring
        var readings = Decode(r);
        Assert.Equal(2032, readings["fault.code.1"].Value);
        Assert.Equal(2063, readings["fault.code.2"].Value);
        Assert.Equal(2092, readings["fault.code.3"].Value);
        Assert.Equal(0, readings["fault.code.4"].Value);
        Assert.Equal("Grid loss", HuaweiCodes.DescribeAlarm(2032));
        Assert.Equal("CT wiring abnormal", HuaweiCodes.DescribeAlarm(2092));
    }

    [Fact]
    public void Identity_ReadsModelSerialAndStrings()
    {
        var id = HuaweiRegisterMap.DecodeIdentity(Registers());
        Assert.Equal(new HuaweiIdentity("SUN2000-10KTL-M1", "HV2150012345", 2, 10_000), id);
    }

    [Fact]
    public void Status_IsNamed()
    {
        Assert.Equal("On grid", HuaweiCodes.DescribeStatus(0x0200));
        Assert.Equal("Standby: no sunlight", HuaweiCodes.DescribeStatus(0xA000));
        Assert.Null(HuaweiCodes.DescribeStatus(0x7777));
    }

    [Fact]
    public void Blocks_StayWithinTheReadLimit()
    {
        var blocks = HuaweiRegisterMap.IdentityBlocks.Concat(HuaweiRegisterMap.InverterBlocks(24))
            .Append(HuaweiRegisterMap.MeterBlock).Concat(HuaweiRegisterMap.BatteryBlocks);
        Assert.All(blocks, b => Assert.True(b.Count <= HuaweiRegisterMap.MaxRegistersPerRead));
    }

    [Fact]
    public async Task Plugin_ReadsEverything()
    {
        await using var server = new FakeModbusServer(new() { [1] = Registers() });
        await using var plugin = new HuaweiSun2000Plugin(HuaweiSun2000PluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = server.Port.ToString(),
        }));

        var readings = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        Assert.Equal(5300, readings["load.power"].Value);
        Assert.Equal(1900, readings["battery.power"].Value);
        Assert.Equal("huawei-127-0-0-1-1", readings["pv.power"].DeviceId);

        var info = await plugin.GetDeviceInfoAsync();
        Assert.Equal(("Huawei", "SUN2000-10KTL-M1", "HV2150012345"), (info.Vendor, info.Model, info.SerialNumber));
    }

    [Fact]
    public async Task Plugin_WithoutMeterOrBattery_StillReads()
    {
        // The inverter refuses the meter and battery addresses (Modbus exception 0x02).
        await using var server = new FakeModbusServer(new() { [0] = Registers(meter: false, battery: false) });
        await using var plugin = new HuaweiSun2000Plugin(HuaweiSun2000PluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = server.Port.ToString(), ["unitId"] = "0",
        }));

        var readings = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        Assert.Equal(9420, readings["pv.power"].Value);
        Assert.DoesNotContain("grid.power", readings.Keys);
        Assert.DoesNotContain("battery.soc", readings.Keys);
    }

    [Theory]
    [InlineData(null, null, "Inverter address is required")]
    [InlineData("bad host", null, "isn't a valid")]
    [InlineData("10.0.0.9", "300", "Modbus address")]
    public void ParseOptions_RejectsInvalid(string? host, string? unit, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => HuaweiSun2000PluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = host, ["unitId"] = unit,
        }));
        Assert.Contains(expected, ex.Message);
    }
}

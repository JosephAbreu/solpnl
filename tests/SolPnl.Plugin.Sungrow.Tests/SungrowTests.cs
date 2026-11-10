using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.Sungrow.Tests;

/// <summary>
/// Decoding against hand-built register sets (documented register numbers; no live capture yet). Values are chosen
/// so a wrong address, scale, sign or word order shows up as a wrong number.
/// </summary>
public class SungrowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    private static ushort S(int v) => unchecked((ushort)(short)v);

    private static void Put32(Dictionary<ushort, ushort> r, ushort at, int value)
    {
        r[at] = (ushort)(value & 0xFFFF);              // low word first
        r[(ushort)(at + 1)] = (ushort)((uint)value >> 16);
    }

    private static Dictionary<ushort, ushort> Blank(SungrowFamily family)
    {
        var r = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in SungrowRegisterMap.IdentityBlocks.Concat(SungrowRegisterMap.Blocks(family)))
            for (var i = 0; i < count; i++) r[(ushort)(start + i)] = 0;
        return r;
    }

    private static void Identity(Dictionary<ushort, ushort> r, ushort type, string serial)
    {
        for (var i = 0; i < serial.Length; i++)
            r[(ushort)(4990 + i / 2)] |= (ushort)(i % 2 == 0 ? serial[i] << 8 : serial[i]);
        r[5000] = type;
        r[5001] = 100; // 10.0 kW
    }

    /// <summary>SG10RT (three phase) with a smart meter: 7.2 kW AC, house using 2.5 kW.</summary>
    internal static Dictionary<ushort, ushort> StringInverter()
    {
        var r = Blank(SungrowFamily.StringInverter);
        Identity(r, 0x2433, "A2290123456");
        r[5003] = 312;                                 // 31.2 kWh today
        r[5008] = 412;                                 // 41.2 °C
        r[5011] = 6120; r[5012] = 64;                  // MPPT 1: 612.0 V, 6.4 A
        r[5013] = 5980; r[5014] = 61;
        Put32(r, 5017, 7480);                          // DC power
        r[5019] = 2301; r[5020] = 2312; r[5021] = 2298;
        r[5022] = 104; r[5023] = 105; r[5024] = 103;
        Put32(r, 5031, 7200);                          // AC power
        r[5036] = 500;                                 // 50.0 Hz
        r[5038] = 0x0000;                              // running
        Put32(r, 5091, 2500);                          // load
        Put32(r, 5093, 154);                           // exported 15.4 kWh today
        Put32(r, 5097, 21);                            // imported 2.1 kWh today
        return r;
    }

    /// <summary>SH5.0RS (single phase) on grid, battery charging at 1.2 kW, exporting 900 W.</summary>
    internal static Dictionary<ushort, ushort> Hybrid(ushort gridState = SungrowRegisterMap.OnGrid)
    {
        var r = Blank(SungrowFamily.Hybrid);
        Identity(r, 0x0D0F, "B2310987654");
        r[5003] = 88;
        r[5008] = 365;
        r[5011] = 3550; r[5012] = 92;
        Put32(r, 5017, 3266);
        r[5019] = 2405; r[5020] = 0; r[5021] = 0;      // single phase
        r[5022] = 61;
        r[13000] = 0x0040;                             // running
        r[13001] = 0x01 | 0x02 | 0x08 | 0x10;          // PV, charging, load, exporting
        Put32(r, 13008, 1150);                         // load
        Put32(r, 13010, 900);                          // export power: + selling
        r[13020] = 2032; r[13021] = 59; r[13022] = 1200; // battery 203.2 V, 5.9 A, 1200 W (magnitudes)
        r[13023] = 645; r[13025] = 221;                // 64.5 %, 22.1 °C
        r[13026] = 31;
        r[13030] = gridState;
        r[13036] = 4;
        return r;
    }

    private static Dictionary<string, Reading> Decode(Dictionary<ushort, ushort> r) =>
        SungrowRegisterMap.Decode(r, SungrowRegisterMap.FamilyOf(r[5000]), "dev", Now).ToDictionary(x => x.Metric);

    [Theory]
    [InlineData("pv.power", 7200, "W")]
    [InlineData("vendor.sungrow.ac_power", 7200, "W")]
    [InlineData("vendor.sungrow.dc_power", 7480, "W")]
    [InlineData("pv.voltage.1", 612.0, "V")]
    [InlineData("pv.current.1", 6.4, "A")]
    [InlineData("pv.power.1", 3916.8, "W")]
    [InlineData("pv.power.2", 3647.8, "W")]
    [InlineData("inverter.voltage.l3", 229.8, "V")]
    [InlineData("inverter.current.l2", 10.5, "A")]
    [InlineData("inverter.frequency", 50.0, "Hz")]
    [InlineData("inverter.temperature", 41.2, "°C")]
    [InlineData("load.power", 2500, "W")]
    [InlineData("grid.power", -4700, "W")]          // 2500 load - 7200 solar: selling
    [InlineData("vendor.sungrow.state", 0, "")]
    [InlineData("vendor.sungrow.energy.pv_today", 31.2, "kWh")]
    [InlineData("vendor.sungrow.energy.grid_export_today", 15.4, "kWh")]
    [InlineData("vendor.sungrow.energy.grid_import_today", 2.1, "kWh")]
    public void StringInverter_WithMeter(string metric, double expected, string unit)
    {
        var reading = Decode(StringInverter())[metric];
        Assert.Equal(expected, reading.Value, 3);
        Assert.Equal(unit, reading.Unit);
    }

    [Fact]
    public void StringInverter_NoGridVoltage()
    {
        // A grid-tied inverter's AC voltage isn't reported as grid voltage: asleep at night it would look like an outage.
        Assert.DoesNotContain(Decode(StringInverter()).Keys, m => m.StartsWith("grid.voltage", StringComparison.Ordinal));
    }

    [Fact]
    public void StringInverter_WithoutMeter_SkipsLoadAndGrid()
    {
        var r = StringInverter();
        r[5091] = 0; r[5092] = 0;
        var readings = Decode(r);
        Assert.DoesNotContain("load.power", readings.Keys);
        Assert.DoesNotContain("grid.power", readings.Keys);
        Assert.Equal(7200, readings["pv.power"].Value);
    }

    [Fact]
    public void StringInverter_ThirtyTwoBitWordOrder()
    {
        var r = StringInverter();
        Put32(r, 5031, 70_000);                         // needs the high word
        Assert.Equal(70_000, Decode(r)["pv.power"].Value);
    }

    [Theory]
    [InlineData("pv.power", 3266, "W")]
    [InlineData("load.power", 1150, "W")]
    [InlineData("grid.power", -900, "W")]
    [InlineData("grid.voltage.l1", 240.5, "V")]
    [InlineData("battery.voltage", 203.2, "V")]
    [InlineData("battery.current", 5.9, "A")]
    [InlineData("battery.power", 1200, "W")]
    [InlineData("battery.soc", 64.5, "%")]
    [InlineData("vendor.sungrow.battery_temperature", 22.1, "°C")]
    [InlineData("vendor.sungrow.state", 0x40, "")]
    [InlineData("inverter.temperature", 36.5, "°C")]
    public void Hybrid_OnGridCharging(string metric, double expected, string unit)
    {
        var reading = Decode(Hybrid())[metric];
        Assert.Equal(expected, reading.Value, 3);
        Assert.Equal(unit, reading.Unit);
    }

    [Fact]
    public void Hybrid_SinglePhase()
    {
        var readings = Decode(Hybrid());
        Assert.DoesNotContain(readings.Keys, m => m.EndsWith(".l2", StringComparison.Ordinal));
    }

    [Fact]
    public void Hybrid_Discharging_IsNegative_WhateverTheRegisterSign()
    {
        var r = Hybrid();
        r[13001] = 0x01 | 0x04 | 0x08;                 // discharging
        r[13022] = S(-800);                            // newer firmware: signed
        r[13021] = S(-39);
        var readings = Decode(r);
        Assert.Equal(-800, readings["battery.power"].Value);
        Assert.Equal(-3.9, readings["battery.current"].Value, 3);
    }

    [Fact]
    public void Hybrid_OffGrid_ReportsZeroGridVoltage()
    {
        var readings = Decode(Hybrid(gridState: SungrowRegisterMap.OffGrid));
        Assert.Equal(0, readings["grid.voltage.l1"].Value);
        Assert.Equal(240.5, readings["inverter.voltage.l1"].Value, 3); // its own backup output
    }

    [Fact]
    public void Hybrid_NoBattery_SkipsBattery()
    {
        var r = Hybrid();
        r[13020] = 0;
        Assert.DoesNotContain(Decode(r).Keys, m => m.StartsWith("battery.", StringComparison.Ordinal));
    }

    [Fact]
    public void UnavailableValues_AreSkipped()
    {
        var r = StringInverter();
        r[5008] = 0x7FFF;
        r[5013] = 0xFFFF;
        var readings = Decode(r);
        Assert.DoesNotContain("inverter.temperature", readings.Keys);
        Assert.DoesNotContain("pv.voltage.2", readings.Keys);
    }

    [Theory]
    [InlineData(0x0D0F, SungrowFamily.Hybrid, "SH5.0RS")]
    [InlineData(0x0E03, SungrowFamily.Hybrid, "SH10RT")]
    [InlineData(0x2433, SungrowFamily.StringInverter, "SG10RT")]
    [InlineData(0x2606, SungrowFamily.StringInverter, "SG5.0RS")]
    [InlineData(0x1234, SungrowFamily.StringInverter, "Sungrow inverter (type 0x1234)")]
    public void Models_AndFamilies(int type, SungrowFamily family, string name)
    {
        Assert.Equal(family, SungrowRegisterMap.FamilyOf((ushort)type));
        Assert.Equal(name, SungrowCodes.DescribeModel((ushort)type));
    }

    [Fact]
    public void States_AreNamed()
    {
        Assert.Equal("Running", SungrowCodes.DescribeState(0x0040));
        Assert.Equal("Fault", SungrowCodes.DescribeState(0x5500));
        Assert.Equal("Running (off grid)", SungrowCodes.DescribeState(0x1000));
        Assert.Null(SungrowCodes.DescribeState(0x7777));
    }

    [Fact]
    public async Task Plugin_DetectsFamilyAndReadsInputRegistersAtWireAddresses()
    {
        // The fake device is keyed by wire address: one less than the documented register number.
        var wire = Hybrid().ToDictionary(kv => (ushort)(kv.Key - 1), kv => kv.Value);
        await using var server = new FakeModbusServer(new() { [1] = wire });
        await using var plugin = new SungrowPlugin(SungrowPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = server.Port.ToString(),
        }));

        var readings = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        Assert.Equal(1200, readings["battery.power"].Value);
        Assert.Equal("sungrow-127-0-0-1", readings["pv.power"].DeviceId);

        var info = await plugin.GetDeviceInfoAsync();
        Assert.Equal(("Sungrow", "SH5.0RS", "B2310987654"), (info.Vendor, info.Model, info.SerialNumber));

        var before = server.Requests; // identity is read once
        await plugin.ReadAsync();
        Assert.Equal(SungrowRegisterMap.Blocks(SungrowFamily.Hybrid).Count, server.Requests - before);
    }

    [Theory]
    [InlineData(null, null, "Inverter address is required")]
    [InlineData("bad host", null, "isn't a valid")]
    [InlineData("10.0.0.8", "300", "Modbus address")]
    public void ParseOptions_RejectsInvalid(string? host, string? unit, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => SungrowPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = host, ["unitId"] = unit,
        }));
        Assert.Contains(expected, ex.Message);
    }
}

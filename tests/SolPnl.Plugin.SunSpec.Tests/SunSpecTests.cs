using SolPnl.Contracts.Models;
using SolPnl.Modbus;
using static SolPnl.Plugin.SunSpec.Tests.SunSpecImage;

namespace SolPnl.Plugin.SunSpec.Tests;

/// <summary>
/// Decoding and discovery against hand-built register maps laid out as in the SunSpec model definitions (no live
/// capture yet). Values are chosen so a wrong offset, scale factor or sign shows up as a wrong number.
/// </summary>
public class SunSpecTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    /// <summary>Split-phase inverter with scale factors (model 102), 4.8 kW producing.</summary>
    internal static ushort[] Inverter102()
    {
        var r = Fill(50, 0xFFFF);
        r[0] = 200; r[1] = 100; r[2] = 100; r[4] = S(-1);
        r[8] = 1201; r[9] = 1199; r[11] = S(-1);
        r[12] = 4800; r[13] = 0;
        r[14] = 6001; r[15] = S(-2);
        r[22] = 0x00BC; r[23] = 0x614E; r[24] = 0;   // 12,345,678 Wh
        r[25] = 1250; r[26] = S(-2);
        r[27] = 4000; r[28] = S(-1);
        r[29] = 5000; r[30] = 0;
        r[31] = 452; r[32] = 0x8000; r[33] = 0; r[34] = 0x8000; r[35] = S(-1);
        r[36] = 4;                                    // MPPT (producing)
        r[37] = 0;
        r[38] = 0; r[39] = 0;                         // no events
        return r;
    }

    /// <summary>Multiple MPPT (model 160), two inputs; the second doesn't report power.</summary>
    internal static ushort[] Mppt()
    {
        var r = new ushort[8 + 2 * 20];
        r[0] = S(-2); r[1] = S(-1); r[2] = 0; r[6] = 2;
        r[8] = 1; r[8 + 9] = 650; r[8 + 10] = 4010; r[8 + 11] = 2600;
        r[28] = 2; r[28 + 9] = 600; r[28 + 10] = 3990; r[28 + 11] = 0xFFFF;
        return r;
    }

    /// <summary>Split-phase meter with scale factors (model 202), exporting 1.5 kW (positive = import).</summary>
    internal static ushort[] Meter202()
    {
        var r = Fill(105, 0x8000);
        r[1] = 50; r[2] = 40; r[4] = S(-1);
        r[6] = 1210; r[7] = 1205; r[13] = S(-1);
        r[14] = 6000; r[15] = S(-2);
        r[16] = S(-1500); r[17] = S(-800); r[18] = S(-700); r[20] = 0;
        r[36] = 0x001E; r[37] = 0x8480;               // exported 2,000,000 Wh
        r[44] = 0x0007; r[45] = 0xA120;               // imported 500,000 Wh
        r[52] = 0;
        return r;
    }

    private static Dictionary<string, Reading> Decode(Action<SunSpecDecoder.Readings> decode)
    {
        var readings = new SunSpecDecoder.Readings("dev", Now);
        decode(readings);
        return readings.List.ToDictionary(r => r.Metric);
    }

    [Theory]
    [InlineData("vendor.sunspec.state", 4, "")]
    [InlineData("inverter.voltage.l1", 120.1, "V")]
    [InlineData("inverter.voltage.l2", 119.9, "V")]
    [InlineData("inverter.current.l1", 10.0, "A")]
    [InlineData("inverter.frequency", 60.01, "Hz")]
    [InlineData("vendor.sunspec.ac_power", 4800, "W")]
    [InlineData("pv.power", 4800, "W")]
    [InlineData("vendor.sunspec.dc_current", 12.5, "A")]
    [InlineData("vendor.sunspec.dc_voltage", 400.0, "V")]
    [InlineData("vendor.sunspec.dc_power", 5000, "W")]
    [InlineData("vendor.sunspec.energy.ac_total", 12345.678, "kWh")]
    [InlineData("inverter.temperature.1", 45.2, "°C")]
    [InlineData("inverter.temperature", 45.2, "°C")]
    [InlineData("fault.code.1", 0, "")]
    public void Inverter_IntegerModel(string metric, double expected, string unit)
    {
        var reading = Decode(r => SunSpecDecoder.DecodeInverter(102, Inverter102(), r))[metric];
        Assert.Equal(expected, reading.Value, 3);
        Assert.Equal(unit, reading.Unit);
    }

    [Fact]
    public void Inverter_SkipsUnimplementedAndMissingPhases()
    {
        var readings = Decode(r => SunSpecDecoder.DecodeInverter(102, Inverter102(), r));
        Assert.DoesNotContain("inverter.voltage.l3", readings.Keys);  // split phase: two legs
        Assert.DoesNotContain("inverter.temperature.2", readings.Keys); // 0x8000: not implemented
        Assert.DoesNotContain("inverter.temperature.3", readings.Keys); // exactly 0 °C: no sensor
        Assert.DoesNotContain(readings.Keys, m => m.StartsWith("grid.", StringComparison.Ordinal)); // only from a meter
    }

    [Fact]
    public void Inverter_EventsBecomeFaultCodes()
    {
        var registers = Inverter102();
        registers[39] = (1 << 7) | (1 << 11); // over temperature, AC under voltage
        var readings = Decode(r => SunSpecDecoder.DecodeInverter(102, registers, r));
        Assert.Equal(8, readings["fault.code.1"].Value);
        Assert.Equal(12, readings["fault.code.2"].Value);
        Assert.Equal(0, readings["fault.code.3"].Value);
        Assert.Equal("Too hot", SunSpecEvents.Describe(8));
        Assert.Equal("Grid voltage too low", SunSpecEvents.Describe(12));
    }

    [Fact]
    public void Inverter_NegativeAcPower_IsNoSolar()
    {
        var registers = Inverter102();
        registers[12] = S(-6); // night-time standby draw
        var readings = Decode(r => SunSpecDecoder.DecodeInverter(102, registers, r));
        Assert.Equal(0, readings["pv.power"].Value);
        Assert.Equal(-6, readings["vendor.sunspec.ac_power"].Value);
    }

    private static ushort[] Inverter111(float watts, ushort state)
    {
        var r = new ushort[60];
        for (var i = 0; i < 46; i += 2) PutFloat(r, i, float.NaN);
        PutFloat(r, 2, 14.0f);
        PutFloat(r, 14, 230.1f);
        PutFloat(r, 20, watts);
        PutFloat(r, 22, 50.0f);
        PutFloat(r, 30, 9_876_500f);
        PutFloat(r, 40, 38.5f);
        r[46] = state;
        return r;
    }

    [Fact]
    public void Inverter_FloatModel()
    {
        var readings = Decode(r => SunSpecDecoder.DecodeInverter(111, Inverter111(3210.5f, 4), r));
        Assert.Equal(3210.5, readings["pv.power"].Value, 3);
        Assert.Equal(230.1, readings["inverter.voltage.l1"].Value, 3);
        Assert.Equal(14.0, readings["inverter.current.l1"].Value, 3);
        Assert.Equal(50.0, readings["inverter.frequency"].Value, 3);
        Assert.Equal(9876.5, readings["vendor.sunspec.energy.ac_total"].Value, 3);
        Assert.Equal(38.5, readings["inverter.temperature"].Value, 3);
        Assert.DoesNotContain("vendor.sunspec.dc_power", readings.Keys); // NaN: not implemented
        Assert.DoesNotContain("inverter.voltage.l2", readings.Keys);
    }

    [Fact]
    public void Inverter_AsleepWithoutPower_ReportsZeroSolar()
    {
        var readings = Decode(r => SunSpecDecoder.DecodeInverter(111, Inverter111(float.NaN, SunSpecStates.Sleeping), r));
        Assert.Equal(0, readings["pv.power"].Value);
        Assert.Equal("Sleeping", SunSpecStates.Describe((int)readings["vendor.sunspec.state"].Value));
    }

    [Fact]
    public void Mppt_PerInput()
    {
        var readings = Decode(r => SunSpecDecoder.DecodeMppt(Mppt(), r));
        Assert.Equal(6.5, readings["pv.current.1"].Value, 3);
        Assert.Equal(401.0, readings["pv.voltage.1"].Value, 3);
        Assert.Equal(2600, readings["pv.power.1"].Value, 3);
        Assert.Equal(2394, readings["pv.power.2"].Value, 3); // not reported: volts × amps
        Assert.DoesNotContain("pv.power.3", readings.Keys);
    }

    [Theory]
    [InlineData(false, -1500, -800, 50, 200)]
    [InlineData(true, 1500, 800, 200, 50)]
    public void Meter_IntegerModel(bool positiveIsExport, double grid, double gridL1, double importKwh, double exportKwh)
    {
        var readings = Decode(r => SunSpecDecoder.DecodeMeter(202, Meter202(), positiveIsExport, r));
        Assert.Equal(grid, readings["grid.power"].Value);
        Assert.Equal(gridL1, readings["grid.power.l1"].Value);
        Assert.Equal(121.0, readings["grid.voltage.l1"].Value, 3);
        Assert.Equal(120.5, readings["grid.voltage.l2"].Value, 3);
        Assert.Equal(5.0, readings["grid.current.l1"].Value, 3);
        Assert.Equal(60.0, readings["grid.frequency"].Value, 3);
        Assert.Equal(importKwh * 10, readings["vendor.sunspec.energy.grid_import_total"].Value, 3);
        Assert.Equal(exportKwh * 10, readings["vendor.sunspec.energy.grid_export_total"].Value, 3);
        Assert.DoesNotContain("grid.voltage.l3", readings.Keys);
    }

    [Fact]
    public void Meter_FloatModel()
    {
        var r = new ushort[124];
        for (var i = 0; i < 124; i += 2) PutFloat(r, i, float.NaN);
        PutFloat(r, 10, 231.0f); PutFloat(r, 12, 229.5f); PutFloat(r, 14, 230.2f);
        PutFloat(r, 24, 50.01f);
        PutFloat(r, 26, 742.0f);
        PutFloat(r, 28, 300f); PutFloat(r, 30, 242f); PutFloat(r, 32, 200f);
        PutFloat(r, 66, 1_234_000f);
        var readings = Decode(x => SunSpecDecoder.DecodeMeter(213, r, false, x));
        Assert.Equal(742, readings["grid.power"].Value, 3);
        Assert.Equal(230.2, readings["grid.voltage.l3"].Value, 3);
        Assert.Equal(200, readings["grid.power.l3"].Value, 3);
        Assert.Equal(1234, readings["vendor.sunspec.energy.grid_import_total"].Value, 3);
        Assert.DoesNotContain("vendor.sunspec.energy.grid_export_total", readings.Keys);
    }

    [Fact]
    public void Load_IsSolarPlusGrid()
    {
        var readings = Decode(r =>
        {
            SunSpecDecoder.DecodeInverter(102, Inverter102(), r);
            SunSpecDecoder.DecodeMeter(202, Meter202(), false, r);
            SunSpecDecoder.AddLoad(r);
        });
        Assert.Equal(3300, readings["load.power"].Value); // 4800 W solar, 1500 W of it exported
    }

    [Fact]
    public void Common_ReadsIdentity()
    {
        var id = SunSpecDecoder.DecodeCommon(Common("Fronius", "Primo 5.0-1", "3.25.4-1", "34112233"));
        Assert.Equal(new SunSpecIdentity("Fronius", "Primo 5.0-1", "3.25.4-1", "34112233"), id);
    }

    [Fact]
    public async Task Discovery_WalksTheModelChain()
    {
        var image = new SunSpecImage().Model(1, Common("SMA", "SB5.0", "1", "123")).Model(102, Inverter102()).Model(160, Mppt()).End();
        var models = await SunSpecDiscovery.DiscoverAsync(image.Reader());
        Assert.Equal([new(1, 40004, 66), new(102, 40072, 50), new(160, 40124, 48)], models);
    }

    [Fact]
    public async Task Discovery_TriesOtherBaseAddresses()
    {
        var image = new SunSpecImage(baseAddress: 0).Model(1, Common("X", "Y", "1", "2")).End();
        var models = await SunSpecDiscovery.DiscoverAsync(image.Reader()); // 40000 answers "illegal address"
        Assert.Equal(1, models.Single().Id);
        Assert.Equal(4, models.Single().Start);
    }

    [Fact]
    public async Task Discovery_NoMarker_ExplainsWhatToCheck()
    {
        var image = new SunSpecImage();
        image.Registers[40000] = 0x1234;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => SunSpecDiscovery.DiscoverAsync(image.Reader()));
        Assert.Contains("Modbus TCP", ex.Message);
    }

    [Fact]
    public async Task ReadModel_SplitsLongModels()
    {
        var data = Enumerable.Range(0, 300).Select(i => (ushort)i).ToArray();
        var image = new SunSpecImage().Model(64110, data).End();
        var reads = new List<ushort>();
        var reader = image.Reader();
        var result = await SunSpecDiscovery.ReadModelAsync((s, c, ct) => { reads.Add(c); return reader(s, c, ct); },
            new SunSpecModel(64110, 40004, 300));
        Assert.Equal(data, result);
        Assert.Equal([125, 125, 50], reads);
    }

    [Fact]
    public async Task Plugin_ReadsInverterAndMeterOnItsOwnAddress()
    {
        var inverter = new SunSpecImage()
            .Model(1, Common("Fronius", "Symo GEN24 8.0 Plus", "1.30", "SN123"))
            .Model(102, Inverter102()).Model(160, Mppt()).End();
        var meter = new SunSpecImage().Model(1, Common("Fronius", "Smart Meter", "1", "M1")).Model(202, Meter202()).End();
        await using var server = new FakeModbusServer(new() { [1] = inverter.Registers, [200] = meter.Registers });

        var options = SunSpecPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = server.Port.ToString(), ["meterUnitId"] = "200",
        });
        await using var plugin = new SunSpecPlugin(options);

        var readings = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        Assert.Equal(4800, readings["pv.power"].Value);
        Assert.Equal(2600, readings["pv.power.1"].Value);
        Assert.Equal(-1500, readings["grid.power"].Value);
        Assert.Equal(3300, readings["load.power"].Value);
        Assert.Equal("sunspec-127-0-0-1-1", readings["pv.power"].DeviceId);

        var info = await plugin.GetDeviceInfoAsync();
        Assert.Equal(("Fronius", "Symo GEN24 8.0 Plus", "SN123", "1.30"), (info.Vendor, info.Model, info.SerialNumber, info.FirmwareVersion));

        // The second poll reuses the discovered layout: one read each for the inverter, MPPT and meter models.
        var before = server.Requests;
        await plugin.ReadAsync();
        Assert.Equal(3, server.Requests - before);
    }

    [Fact]
    public async Task Plugin_DeviceInfoWithoutConnection()
    {
        await using var plugin = new SunSpecPlugin(SunSpecPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "Inverter.LAN", ["unitId"] = "3",
        }));
        var info = await plugin.GetDeviceInfoAsync();
        Assert.Equal("sunspec-inverter-lan-3", info.DeviceId);
    }

    [Theory]
    [InlineData(null, null, null, "Inverter address is required")]
    [InlineData("bad host", null, null, "isn't a valid")]
    [InlineData("10.0.0.5", "256", null, "Modbus address")]
    [InlineData("10.0.0.5", null, "Sideways", "Meter direction")]
    public void ParseOptions_RejectsInvalid(string? host, string? unit, string? direction, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => SunSpecPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = host, ["unitId"] = unit, ["meterDirection"] = direction,
        }));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ParseOptions_Defaults()
    {
        var o = SunSpecPluginType.ParseOptions(new Dictionary<string, string?> { ["host"] = "10.0.0.5", ["meterUnitId"] = "1" });
        Assert.Equal((502, (byte)1, (byte?)null, false), (o.Port, o.UnitId, o.MeterUnitId, o.MeterPositiveIsExport));
    }
}

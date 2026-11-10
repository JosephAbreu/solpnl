using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.SolarmanDeye.Tests;

/// <summary>
/// Decoding tests against a hand-built register image (no live capture yet): values are chosen so a wrong address,
/// scale, sign or word order shows up as a wrong number.
/// </summary>
public class DeyeRegisterMapTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    internal static Dictionary<ushort, ushort> Image(bool battery = true)
    {
        var r = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in DeyeRegisterMap.Blocks)
            for (var i = 0; i < count; i++) r[(ushort)(start + i)] = 0;

        // Serial "2306123456" in 3..7, rated power 12 kW (120000 × 0.1, low word first), 2 MPPTs, one phase.
        ushort Chars(char a, char b) => (ushort)(a << 8 | b);
        r[3] = Chars('2', '3'); r[4] = Chars('0', '6'); r[5] = Chars('1', '2'); r[6] = Chars('3', '4'); r[7] = Chars('5', '6');
        r[16] = 120000 & 0xFFFF; r[17] = 120000 >> 16;
        r[18] = 0x0201;
        r[22] = 0x1A0B; r[23] = 0x0910; r[24] = 0x1E05; // 2026-11-09 16:30:05

        r[59] = 2;               // normal
        r[79] = 6000;            // 60.00 Hz
        r[150] = 1211; r[151] = 1198;
        r[160] = 525; r[161] = unchecked((ushort)-310);
        r[167] = 640; r[168] = unchecked((ushort)-372); r[169] = 268;
        r[154] = 1205; r[155] = 1202;
        r[164] = 1010; r[165] = 990;
        r[193] = 5999;
        r[176] = 1200; r[177] = 1150; r[178] = 2350;
        r[179] = 1000; r[180] = 957;
        r[90] = 1000 + 412;      // 41.2 °C
        r[91] = 1000 + 538;      // 53.8 °C
        r[166] = 0;

        r[109] = 3802; r[110] = 81; r[186] = 3080;
        r[111] = 3750; r[112] = 62; r[187] = 2325;
        r[113] = 999; r[114] = 99; r[188] = 999; // third MPPT: not on this model, must be ignored

        if (battery)
        {
            r[183] = 5312;                         // 53.12 V
            r[184] = 87;
            r[190] = unchecked((ushort)-1500);     // Deye: negative = charging
            r[191] = unchecked((ushort)-2824);     // -28.24 A
            r[182] = 1000 + 255;                   // 25.5 °C
        }

        r[108] = 183;                              // 18.3 kWh today
        r[96] = 0x2345; r[97] = 0x0001;            // (0x12345 = 74565) × 0.1 = 7456.5 kWh
        r[78] = 500; r[80] = 1;                    // (65536 + 500) × 0.1 = 6603.6 kWh

        return r;
    }

    private static Dictionary<string, Reading> Decode(Dictionary<ushort, ushort> registers, bool splitPhase = true) =>
        DeyeRegisterMap.Decode(registers, "dev", Now, splitPhase).ToDictionary(r => r.Metric);

    [Fact]
    public void Blocks_AreSmallAndCoverEveryRegisterUsed()
    {
        Assert.All(DeyeRegisterMap.Blocks, b => Assert.True(b.Count is > 0 and <= 32));
        var covered = DeyeRegisterMap.Blocks.SelectMany(b => Enumerable.Range(b.Start, b.Count)).ToHashSet();
        foreach (var address in new[] { 3, 7, 16, 17, 18, 22, 24, 59, 70, 86, 90, 97, 103, 106, 108, 116, 150, 169, 179, 180, 191, 193 })
            Assert.Contains(address, covered);
    }

    [Theory]
    [InlineData("vendor.deye.state", 2, "")]
    [InlineData("grid.frequency", 60.0, "Hz")]
    [InlineData("grid.voltage.l1", 121.1, "V")]
    [InlineData("grid.voltage.l2", 119.8, "V")]
    [InlineData("grid.current.l1", 5.25, "A")]
    [InlineData("grid.current.l2", -3.1, "A")]
    [InlineData("grid.power.l1", 640, "W")]
    [InlineData("grid.power.l2", -372, "W")]
    [InlineData("grid.power", 268, "W")]
    [InlineData("inverter.voltage.l1", 120.5, "V")]
    [InlineData("inverter.current.l2", 9.9, "A")]
    [InlineData("inverter.frequency", 59.99, "Hz")]
    [InlineData("load.power.l1", 1200, "W")]
    [InlineData("load.power.l2", 1150, "W")]
    [InlineData("load.power", 2350, "W")]
    [InlineData("load.current.l2", 9.57, "A")]
    [InlineData("pv.voltage.1", 380.2, "V")]
    [InlineData("pv.current.1", 8.1, "A")]
    [InlineData("pv.power.1", 3080, "W")]
    [InlineData("pv.power.2", 2325, "W")]
    [InlineData("pv.power", 5405, "W")]
    [InlineData("inverter.temperature.1", 41.2, "°C")]
    [InlineData("inverter.temperature.2", 53.8, "°C")]
    [InlineData("inverter.temperature", 53.8, "°C")]
    [InlineData("battery.voltage", 53.12, "V")]
    [InlineData("battery.soc", 87, "%")]
    [InlineData("battery.power", 1500, "W")]
    [InlineData("battery.current", 28.24, "A")]
    [InlineData("vendor.deye.battery_temperature", 25.5, "°C")]
    [InlineData("vendor.deye.energy.pv_today", 18.3, "kWh")]
    [InlineData("vendor.deye.energy.pv_total", 7456.5, "kWh")]
    [InlineData("vendor.deye.energy.grid_import_total", 6603.6, "kWh")]
    public void Decode_SplitPhase(string metric, double expected, string unit)
    {
        var reading = Decode(Image())[metric];
        Assert.Equal(expected, reading.Value, 3);
        Assert.Equal(unit, reading.Unit);
    }

    [Fact]
    public void Decode_OnlyReportsTheInverterOwnMppts()
    {
        var readings = Decode(Image());
        Assert.DoesNotContain("pv.power.3", readings.Keys);
        Assert.DoesNotContain("pv.voltage.3", readings.Keys);
    }

    [Fact]
    public void Decode_SinglePhase_HasNoSecondLeg()
    {
        // A single-phase unit's L2 registers read 0; reporting them would look like a dead grid leg.
        var readings = Decode(Image(), splitPhase: false);
        Assert.Contains("grid.voltage.l1", readings.Keys);
        Assert.DoesNotContain(readings.Keys, m => m.EndsWith(".l2", StringComparison.Ordinal));
        Assert.Equal(2350, readings["load.power"].Value);
    }

    [Fact]
    public void Decode_NoBattery_SkipsBatteryMetrics()
    {
        var readings = Decode(Image(battery: false));
        Assert.DoesNotContain(readings.Keys, m => m.StartsWith("battery.", StringComparison.Ordinal));
        Assert.DoesNotContain("vendor.deye.battery_temperature", readings.Keys);
    }

    [Fact]
    public void Decode_NoFaults_ReportsEmptySlots()
    {
        var readings = Decode(Image());
        for (var slot = 1; slot <= DeyeRegisterMap.FaultSlots; slot++)
            Assert.Equal(0, readings[$"fault.code.{slot}"].Value);
    }

    [Fact]
    public void Decode_FaultBits_BecomeFCodes()
    {
        var registers = Image();
        registers[103] = 1 << 12;        // F13
        registers[104] = 1 << 6;         // F23
        registers[106] = 1 << 15;        // F64
        var readings = Decode(registers);
        Assert.Equal(13, readings["fault.code.1"].Value);
        Assert.Equal(23, readings["fault.code.2"].Value);
        Assert.Equal(64, readings["fault.code.3"].Value);
        Assert.Equal(0, readings["fault.code.4"].Value);
        Assert.Equal("Heat sink too hot", DeyeFaults.Describe(64));
    }

    [Fact]
    public void DecodeIdentity_ReadsSerialRatingAndMppts()
    {
        var identity = DeyeRegisterMap.DecodeIdentity(Image());
        Assert.Equal("2306123456", identity.SerialNumber);
        Assert.Equal(12000, identity.RatedPowerWatts);
        Assert.Equal(2, identity.MpptCount);
    }

    [Fact]
    public void DecodeIdentity_UnknownMpptCount_FallsBackToDefault()
    {
        var registers = Image();
        registers[18] = 0;
        Assert.Null(DeyeRegisterMap.DecodeIdentity(registers).MpptCount);
        Assert.Contains("pv.power.2", Decode(registers).Keys);
        Assert.DoesNotContain("pv.power.3", Decode(registers).Keys);
    }

    [Fact]
    public void DecodeClock_UnpacksBytes()
    {
        Assert.Equal(new DateTime(2026, 11, 9, 16, 30, 5), DeyeRegisterMap.DecodeClock(Image()));
        var bad = Image();
        bad[22] = 0x1A0D; // month 13
        Assert.Null(DeyeRegisterMap.DecodeClock(bad));
    }

    [Theory]
    [InlineData(2, "Normal")]
    [InlineData(4, "Fault")]
    [InlineData(9, null)]
    public void States_AreNamed(int value, string? expected) => Assert.Equal(expected, DeyeStates.Describe(value));
}

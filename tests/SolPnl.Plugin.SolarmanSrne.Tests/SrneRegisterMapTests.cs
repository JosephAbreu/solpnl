using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.SolarmanSrne.Tests;

public class SrneRegisterMapTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 20, 27, 4, TimeSpan.Zero);

    // Captured from a live Sungoldpower SPH10048P (no-battery mode) on 2026-11-07.
    private static readonly ushort[] Block0100 =
        [0x0000, 0x0002, 0xFFFC, 0, 0, 0, 0, 0x089F, 0x002C, 0x03D1, 0, 0, 0, 0, 0, 0x0856, 0x0008, 0x00B2];

    private static readonly ushort[] Block0200 =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1A0B, 0x070E, 0x1B04, 0x0000,
        0x0002, 0x0000, 0x144D, 0x04C1, 0x000D, 0x1770, 0x04C8, 0x002D, 0x1770, 0x0031, 0x0000, 0x0243, 0x025D, 0, 0, 0x000C,
    ];

    private static readonly ushort[] Block0220 =
    [
        0x0190, 0x013C, 0x0132, 0x04B0, 0x0004, 0, 0, 0, 0x0A21, 0x0A2D, 0x04C3, 0x0000, 0x04C5, 0x0000, 0x002E, 0x0000,
        0x003D, 0x0000, 0x02E3, 0x0000, 0x02FC, 0x0000, 0x000F, 0x0000, 0x0015, 0x0000, 0x003F, 0x00DF, 0x0000, 0x0085, 0x010B, 0x0000,
    ];

    private static Dictionary<ushort, ushort> Captured()
    {
        var registers = new Dictionary<ushort, ushort>();
        void Load(ushort start, ushort[] values)
        {
            for (var i = 0; i < values.Length; i++) registers[(ushort)(start + i)] = values[i];
        }
        Load(0x0100, Block0100);
        Load(0x0200, Block0200);
        Load(0x0220, Block0220);
        return registers;
    }

    private static Dictionary<string, Reading> DecodeCaptured(Dictionary<ushort, ushort>? registers = null) =>
        SrneRegisterMap.Decode(registers ?? Captured(), "dev", Now).ToDictionary(r => r.Metric);

    [Fact]
    public void BlocksMatchCapturedLengths()
    {
        Assert.Equal([(0x0100, (ushort)Block0100.Length), (0x0200, (ushort)Block0200.Length), (0x0220, (ushort)Block0220.Length)],
            SrneRegisterMap.Blocks.Select(b => ((int)b.Start, b.Count)));
        Assert.All(SrneRegisterMap.Blocks, b => Assert.True(b.Count <= 32));
    }

    [Theory]
    [InlineData("pv.voltage.1", 220.7, "V")]
    [InlineData("pv.current.1", 4.4, "A")]
    [InlineData("pv.power.1", 977, "W")]
    [InlineData("pv.voltage.2", 213.4, "V")]
    [InlineData("pv.current.2", 0.8, "A")]
    [InlineData("pv.power.2", 178, "W")]
    [InlineData("pv.power", 1155, "W")]
    [InlineData("grid.voltage.l1", 121.7, "V")]
    [InlineData("grid.voltage.l2", 121.9, "V")]
    [InlineData("grid.frequency", 60.0, "Hz")]
    [InlineData("inverter.voltage.l1", 122.4, "V")]
    [InlineData("inverter.voltage.l2", 122.1, "V")]
    [InlineData("inverter.frequency", 60.0, "Hz")]
    [InlineData("load.power.l1", 579, "W")]
    [InlineData("load.power.l2", 739, "W")]
    [InlineData("load.power", 1318, "W")]
    [InlineData("load.apparent_power", 1369, "VA")]
    [InlineData("load.current.l1", 4.9, "A")]
    [InlineData("load.current.l2", 6.1, "A")]
    [InlineData("inverter.temperature.1", 40.0, "°C")]
    [InlineData("inverter.temperature", 40.0, "°C")]
    [InlineData("vendor.srne.state", 2, "")]
    public void Decode_CapturedLiveValues(string metric, double expected, string unit)
    {
        var readings = DecodeCaptured();
        Assert.True(readings.TryGetValue(metric, out var reading), $"missing {metric}");
        Assert.Equal(expected, reading.Value, precision: 3);
        Assert.Equal(unit, reading.Unit);
        Assert.Equal("dev", reading.DeviceId);
        Assert.Equal(Now, reading.Timestamp);
    }

    [Fact]
    public void Decode_NoBatteryMode_OmitsBatteryMetrics()
    {
        var readings = DecodeCaptured();
        Assert.DoesNotContain(readings.Keys, k => k.StartsWith("battery."));
    }

    [Fact]
    public void Decode_BatteryPresent_IncludesBatteryMetrics()
    {
        var registers = Captured();
        registers[0x0100] = 87;           // 87 %
        registers[0x0101] = 532;          // 53.2 V
        registers[0x0102] = 0xFF9C;       // -10.0 A

        var readings = DecodeCaptured(registers);
        Assert.Equal(87, readings[Metrics.BatterySoc].Value);
        Assert.Equal(53.2, readings[Metrics.BatteryVoltage].Value, precision: 3);
        Assert.Equal(-10.0, readings[Metrics.BatteryCurrent].Value, precision: 3);
    }

    [Fact]
    public void DecodeClock_ReturnsInverterLocalTime()
    {
        Assert.Equal(new DateTime(2026, 11, 7, 14, 27, 4), SrneRegisterMap.DecodeClock(Captured()));
    }

    [Fact]
    public void DecodeClock_InvalidValues_ReturnsNull()
    {
        var registers = Captured();
        registers[0x020C] = 0x1A0D; // month 13
        Assert.Null(SrneRegisterMap.DecodeClock(registers));
    }
}

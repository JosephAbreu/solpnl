using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.Luxpower.Tests;

public class LuxpowerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);
    private const string Datalog = "2222222222";
    private const string Inverter = "5555555555";

    // ---- Framing: golden frames from lxp-bridge's tests (tests/test_packet_tcp_frames.rs) ----

    [Fact]
    public void BuildReadRequest_MatchesLxpBridgeReadInputs()
    {
        byte[] expected =
        [
            161, 26, 1, 0, 32, 0, 1, 194, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 18, 0, 0, 4,
            53, 53, 53, 53, 53, 53, 53, 53, 53, 53, 0, 0, 40, 0, 42, 132,
        ];
        Assert.Equal(expected, LuxpowerClient.BuildReadRequest(Datalog, Inverter, LuxpowerClient.ReadInput, 0, 40));
    }

    [Fact]
    public void BuildReadRequest_MatchesLxpBridgeReadHold()
    {
        var frame = LuxpowerClient.BuildReadRequest(Datalog, Inverter, LuxpowerClient.ReadHolding, 12, 3);
        Assert.Equal(new byte[] { 12, 0, 3, 0, 112, 38 }, frame[^6..]);
        Assert.Equal(new byte[] { 161, 26, 1, 0, 32, 0, 1, 194 }, frame[..8]);
    }

    /// <summary>A protocol-2 read reply, laid out as lxp-bridge decodes it (byte count, then LE values).</summary>
    internal static byte[] Reply(byte function, ushort register, ushort[] values, string inverter = Inverter, bool badCrc = false)
    {
        var data = new byte[17 + values.Length * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)(data.Length + 2 - 2));
        data[2] = 1;
        data[3] = function;
        Encoding.ASCII.GetBytes(inverter, data.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), register);
        data[16] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(17 + i * 2), values[i]);

        var frame = new byte[18 + data.Length + 2];
        frame[0] = 0xA1; frame[1] = 0x1A;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)(frame.Length - 6));
        frame[6] = 1;
        frame[7] = LuxpowerClient.TcpTranslatedData;
        Encoding.ASCII.GetBytes(Datalog, frame.AsSpan(8));
        data.CopyTo(frame, 18);
        var crc = LuxpowerClient.Crc16(data.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), badCrc ? (ushort)~crc : crc);
        return frame;
    }

    [Fact]
    public void ParseReadReply_ReadsLittleEndianValues()
    {
        var reply = LuxpowerClient.ParseReadReply(Reply(LuxpowerClient.ReadInput, 40, [0x1234, 7, 65535]));
        Assert.NotNull(reply);
        Assert.Equal((LuxpowerClient.ReadInput, Inverter, (ushort)40), (reply.Function, reply.InverterSerial, reply.Register));
        Assert.Equal(new ushort[] { 0x1234, 7, 65535 }, reply.Values);
    }

    [Fact]
    public void ParseReadReply_RejectsBadChecksum() =>
        Assert.Null(LuxpowerClient.ParseReadReply(Reply(LuxpowerClient.ReadInput, 0, [1, 2], badCrc: true)));

    // ---- Decoding ----

    /// <summary>EG4 18kPV-style: 2 strings, charging the battery, exporting, split-phase backup output.</summary>
    internal static Dictionary<ushort, ushort> Registers(bool battery = true)
    {
        var r = new Dictionary<ushort, ushort>();
        for (ushort a = 0; a < 160; a++) r[a] = 0;
        r[0] = 0x14;                              // solar and battery, on grid
        r[1] = 3801; r[2] = 3655;                 // PV volts
        r[7] = 4200; r[8] = 3100;                 // PV watts
        if (battery)
        {
            r[4] = 532;                           // 53.2 V
            r[5] = (99 << 8) | 76;                // SOH 99, SOC 76
            r[10] = 2128;                         // charging
            r[11] = 0;
            r[67] = 24;
        }
        r[12] = 2412;                             // grid 241.2 V
        r[15] = 6001;
        r[16] = 5600;                             // inverter output
        r[17] = 0;                                // rectifier
        r[20] = 2405; r[23] = 6000; r[24] = 1800;
        r[26] = 1500;                             // to grid (export)
        r[27] = 0;                                // to user (import)
        r[28] = 120; r[29] = 98; r[30] = 0;       // PV today 21.8 kWh
        r[33] = 64; r[36] = 41; r[37] = 7;
        r[64] = 41; r[65] = 47; r[66] = 0;        // inside, heat sink 1, (no heat sink 2)
        r[123] = 0;
        r[127] = 1203; r[128] = 1201;             // backup L1, L2
        return r;
    }

    private static Dictionary<string, Reading> Decode(Dictionary<ushort, ushort> r) =>
        LuxpowerRegisterMap.Decode(r, "dev", Now).ToDictionary(x => x.Metric);

    [Theory]
    [InlineData("vendor.luxpower.status", 0x14, "")]
    [InlineData("pv.voltage.1", 380.1, "V")]
    [InlineData("pv.power.1", 4200, "W")]
    [InlineData("pv.current.1", 11.05, "A")]
    [InlineData("pv.power", 7300, "W")]
    [InlineData("grid.power", -1500, "W")]
    [InlineData("grid.voltage", 241.2, "V")]
    [InlineData("grid.frequency", 60.01, "Hz")]
    [InlineData("load.power", 4100, "W")]               // 5600 out - 0 rectifier - 1500 exported
    [InlineData("inverter.voltage.l1", 120.3, "V")]
    [InlineData("inverter.voltage.l2", 120.1, "V")]
    [InlineData("vendor.luxpower.eps_power", 1800, "W")]
    [InlineData("battery.voltage", 53.2, "V")]
    [InlineData("battery.soc", 76, "%")]
    [InlineData("battery.power", 2128, "W")]
    [InlineData("battery.current", 40.0, "A")]
    [InlineData("vendor.luxpower.battery_temperature", 24, "°C")]
    [InlineData("inverter.temperature", 47, "°C")]
    [InlineData("vendor.luxpower.energy.pv_today", 21.8, "kWh")]
    [InlineData("vendor.luxpower.energy.grid_export_today", 4.1, "kWh")]
    [InlineData("vendor.luxpower.energy.battery_charge_today", 6.4, "kWh")]
    [InlineData("fault.code.1", 0, "")]
    public void Decode_Hybrid(string metric, double expected, string unit)
    {
        var reading = Decode(Registers())[metric];
        Assert.Equal(expected, reading.Value, 3);
        Assert.Equal(unit, reading.Unit);
    }

    [Fact]
    public void Decode_SkipsAbsentThirdStringAndHeatSink()
    {
        var readings = Decode(Registers());
        Assert.DoesNotContain("pv.voltage.3", readings.Keys);
        Assert.DoesNotContain("inverter.temperature.3", readings.Keys);
        Assert.DoesNotContain("inverter.voltage", readings.Keys); // per-leg instead
    }

    [Fact]
    public void Decode_ImportingAndDischarging()
    {
        var r = Registers();
        r[7] = 0; r[8] = 0;
        r[10] = 0; r[11] = 3000;                  // discharging
        r[16] = 3000; r[26] = 0; r[27] = 900;     // importing
        var readings = Decode(r);
        Assert.Equal(-3000, readings["battery.power"].Value);
        Assert.Equal(900, readings["grid.power"].Value);
        Assert.Equal(3900, readings["load.power"].Value);
    }

    [Fact]
    public void Decode_GridCharging_LoadExcludesTheRectifier()
    {
        var r = Registers();
        r[16] = 0; r[17] = 2000;                  // rectifier: grid → battery
        r[26] = 0; r[27] = 2600;                  // 2.6 kW from the grid
        Assert.Equal(600, Decode(r)["load.power"].Value);
    }

    [Fact]
    public void Decode_NoBattery_SkipsBattery()
    {
        Assert.DoesNotContain(Decode(Registers(battery: false)).Keys, m => m.StartsWith("battery.", StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_FaultsAndWarnings()
    {
        var r = Registers();
        r[60] = 1 << 3;                           // E003 CT fail (low word)
        r[61] = 1 << (21 - 16);                   // E021 PV voltage high (high word)
        r[63] = 1 << 0;                           // W016 grid power outage
        r[62] = 1 << 6;                           // W006: reserved, not reported
        var readings = Decode(r);
        Assert.Equal(103, readings["fault.code.1"].Value);
        Assert.Equal(121, readings["fault.code.2"].Value);
        Assert.Equal(216, readings["fault.code.3"].Value);
        Assert.Equal(0, readings["fault.code.4"].Value);
        Assert.Equal("E021: PV voltage too high", LuxpowerCodes.Describe(121));
        Assert.Equal("W016: Grid power outage", LuxpowerCodes.Describe(216));
        Assert.Null(LuxpowerCodes.Describe(206));
    }

    [Fact]
    public void Status_IsNamed()
    {
        Assert.Equal("Battery, off grid", LuxpowerCodes.DescribeStatus(0x40));
        Assert.Null(LuxpowerCodes.DescribeStatus(0x77));
    }

    // ---- Settings ----

    [Theory]
    [InlineData(null, "BA12345678", "4512345678", "Dongle address is required")]
    [InlineData("10.0.0.5", "BA123", "4512345678", "Dongle serial number")]
    [InlineData("10.0.0.5", "BA12345678", "45-1234567", "Inverter serial number")]
    public void ParseOptions_RejectsInvalid(string? host, string datalog, string inverter, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => LuxpowerPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = host, ["datalogSerial"] = datalog, ["inverterSerial"] = inverter,
        }));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ParseOptions_Defaults()
    {
        var o = LuxpowerPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "10.0.0.5", ["datalogSerial"] = " ba12345678 ", ["inverterSerial"] = "4512345678",
        });
        Assert.Equal((8000, "BA12345678"), (o.Port, o.DatalogSerial));
    }

    // ---- End to end against a fake dongle ----

    [Fact]
    public async Task Plugin_ReadsThroughHeartbeatsAndUnrelatedFrames()
    {
        var registers = Registers();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var heartbeat = new byte[] { 161, 26, 2, 0, 13, 0, 1, 193, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 0 };
        var echoes = 0;

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            for (var n = 0; n < LuxpowerRegisterMap.Blocks.Count; n++)
            {
                var head = new byte[6];
                await stream.ReadExactlyAsync(head);
                var request = new byte[6 + BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4))];
                head.CopyTo(request, 0);
                await stream.ReadExactlyAsync(request.AsMemory(6));
                if (request[7] == 193) { echoes++; n--; continue; } // our heartbeat, echoed back

                var start = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(32));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(34));
                if (n == 0)
                {
                    await stream.WriteAsync(heartbeat);                                           // must be answered
                    await stream.WriteAsync(Reply(LuxpowerClient.ReadHolding, 0, new ushort[40])); // not ours
                }
                var values = Enumerable.Range(start, count).Select(a => registers[(ushort)a]).ToArray();
                await stream.WriteAsync(Reply(request[21], start, values));
            }
        });

        await using var plugin = new LuxpowerPlugin(LuxpowerPluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = port.ToString(), ["datalogSerial"] = Datalog, ["inverterSerial"] = Inverter,
        }));
        var readings = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        await server;

        Assert.Equal(7300, readings["pv.power"].Value);
        Assert.Equal(120.1, readings["inverter.voltage.l2"].Value, 3); // from the fourth block
        Assert.Equal("luxpower-5555555555", readings["pv.power"].DeviceId);
        Assert.Equal(1, echoes);
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SolPnl.Solarman;

namespace SolPnl.Plugin.SolarmanDeye.Tests;

/// <summary>Runs the plugin end to end against a fake Solarman V5 logger that serves a Deye register image.</summary>
public class FakeLoggerTests
{
    private const uint Serial = 2712345678;

    [Fact]
    public async Task ReadAsync_ReadsEveryBlockAndDecodes()
    {
        var image = DeyeRegisterMapTests.Image();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requested = new List<(ushort Start, ushort Count)>();

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            for (var n = 0; n < DeyeRegisterMap.Blocks.Count; n++)
            {
                var request = await ReadV5Frame(stream);
                var modbus = request.AsSpan(26, request.Length - 28);
                var start = BinaryPrimitives.ReadUInt16BigEndian(modbus[2..]);
                var count = BinaryPrimitives.ReadUInt16BigEndian(modbus[4..]);
                requested.Add((start, count));

                var body = new byte[3 + count * 2];
                body[0] = modbus[0];
                body[1] = modbus[1];
                body[2] = (byte)(count * 2);
                for (var i = 0; i < count; i++)
                    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3 + i * 2), image[(ushort)(start + i)]);
                await stream.WriteAsync(BuildResponse(request[5], WithCrc(body)));
            }
        });

        var options = SolarmanDeyePluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["loggerSerial"] = Serial.ToString(), ["port"] = port.ToString(),
            ["phases"] = SolarmanDeyePluginType.SplitPhase,
        });
        await using var plugin = new SolarmanDeyePlugin(options);

        var readings = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        await server;

        Assert.Equal(DeyeRegisterMap.Blocks, requested);
        Assert.Equal(5405, readings["pv.power"].Value);
        Assert.Equal(119.8, readings["grid.voltage.l2"].Value, 3);
        Assert.Equal("deye-2712345678", readings["pv.power"].DeviceId);
        Assert.Equal(new DateTime(2026, 11, 9, 16, 30, 5), plugin.LastInverterClock);

        var info = await plugin.GetDeviceInfoAsync();
        Assert.Equal("2306123456", info.SerialNumber);
        Assert.Equal("Hybrid inverter 12 kW", info.Model);
    }

    private static byte[] WithCrc(byte[] body)
    {
        var frame = new byte[body.Length + 2];
        body.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(body.Length), ModbusRtu.Crc16(body));
        return frame;
    }

    private static async Task<byte[]> ReadV5Frame(NetworkStream stream)
    {
        var head = new byte[3];
        await stream.ReadExactlyAsync(head);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(1));
        var frame = new byte[11 + length + 2];
        head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(3));
        return frame;
    }

    private static byte[] BuildResponse(byte sequenceLow, byte[] modbus)
    {
        var payloadLength = 14 + modbus.Length;
        var frame = new byte[11 + payloadLength + 2];
        frame[0] = 0xA5;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), 0x1510);
        frame[5] = sequenceLow;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), Serial);
        frame[11] = 0x02;
        frame[12] = 0x01;
        modbus.CopyTo(frame, 25);
        var sum = 0;
        for (var i = 1; i < frame.Length - 2; i++) sum += frame[i];
        frame[^2] = (byte)sum;
        frame[^1] = 0x15;
        return frame;
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SolPnl.Plugin.SolarmanSrne.Protocol;

namespace SolPnl.Plugin.SolarmanSrne.Tests;

public class ModbusRtuTests
{
    [Fact]
    public void BuildReadRequest_MatchesKnownFrame()
    {
        // Well-known reference frame: slave 1, read holding registers 0x0000, count 10 -> CRC C5 CD
        var frame = ModbusRtu.BuildReadRequest(1, ModbusRtu.ReadHoldingRegisters, 0x0000, 10);
        Assert.Equal("01030000000AC5CD", Convert.ToHexString(frame));
    }

    [Fact]
    public void ParseReadResponse_ReturnsRegisters()
    {
        var response = WithCrc([0x01, 0x03, 0x04, 0x00, 0x64, 0x01, 0xF4]);
        var registers = ModbusRtu.ParseReadResponse(response, 1, 0x03, 2);
        Assert.Equal(new ushort[] { 100, 500 }, registers);
    }

    [Fact]
    public void ParseReadResponse_ExceptionFrame_Throws()
    {
        var response = WithCrc([0x01, 0x83, 0x02]);
        var ex = Assert.Throws<ModbusException>(() => ModbusRtu.ParseReadResponse(response, 1, 0x03, 2));
        Assert.Equal(0x02, ex.ExceptionCode);
    }

    [Fact]
    public void ParseReadResponse_BadCrc_Throws()
    {
        var response = WithCrc([0x01, 0x03, 0x02, 0x00, 0x01]);
        response[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => ModbusRtu.ParseReadResponse(response, 1, 0x03, 1));
    }

    internal static byte[] WithCrc(byte[] body)
    {
        var frame = new byte[body.Length + 2];
        body.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(body.Length), ModbusRtu.Crc16(body));
        return frame;
    }
}

public class SolarmanV5ClientTests
{
    private const uint Serial = 2712345678;

    [Fact]
    public async Task ReadRegisters_RoundTripsThroughFakeLogger()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var request = await ReadV5Frame(stream);

            // Validate the request the client sent.
            Assert.Equal(0xA5, request[0]);
            Assert.Equal(0x4510, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(3)));
            Assert.Equal(Serial, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(7)));
            Assert.Equal(0x02, request[11]);
            var modbusRequest = request.AsSpan(26, request.Length - 28).ToArray();
            Assert.Equal(ModbusRtu.BuildReadRequest(1, 0x03, 0x0100, 2), modbusRequest);

            // Send an unrelated frame first (as a logger heartbeat would), then the real response.
            await stream.WriteAsync(BuildResponse(0x4710, request[5], []));
            var modbusResponse = ModbusRtuTests.WithCrc([0x01, 0x03, 0x04, 0x00, 0x5A, 0x02, 0x0B]);
            await stream.WriteAsync(BuildResponse(0x1510, request[5], modbusResponse));
        });

        await using var client = new SolarmanV5Client("127.0.0.1", Serial, port, TimeSpan.FromSeconds(5));
        var registers = await client.ReadRegistersAsync(1, 0x03, 0x0100, 2);

        Assert.Equal(new ushort[] { 90, 523 }, registers);
        await server;
    }

    [Fact]
    public async Task ReadRegisters_EmptyModbusPayload_ThrowsHelpfulError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var request = await ReadV5Frame(stream);
            await stream.WriteAsync(BuildResponse(0x1510, request[5], []));
        });

        await using var client = new SolarmanV5Client("127.0.0.1", Serial, port, TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadRegistersAsync(1, 0x03, 0x0100, 2));
        Assert.Contains("no Modbus data", ex.Message);
        await server;
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

    private static byte[] BuildResponse(ushort control, byte sequenceLow, byte[] modbus)
    {
        var payloadLength = 14 + modbus.Length;
        var frame = new byte[11 + payloadLength + 2];
        frame[0] = 0xA5;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), control);
        frame[5] = sequenceLow;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), Serial);
        frame[11] = 0x02; // frame type
        frame[12] = 0x01; // status
        modbus.CopyTo(frame, 25);
        var sum = 0;
        for (var i = 1; i < frame.Length - 2; i++) sum += frame[i];
        frame[^2] = (byte)sum;
        frame[^1] = 0x15;
        return frame;
    }
}

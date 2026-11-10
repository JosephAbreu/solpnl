using System.Buffers.Binary;
using System.Net.Sockets;

namespace SolPnl.Solarman;

/// <summary>
/// Talks to a Solarman (IGEN) WiFi stick logger such as the LSW-5 on its local TCP port (default 8899)
/// using the "Solarman V5" protocol, which wraps Modbus RTU frames addressed to the inverter.
///
/// Frame layout (little-endian unless noted):
///   0xA5 | length(2) | control(2) | sequence(2) | logger serial(4) | payload(length) | checksum(1) | 0x15
/// Request payload:  frame type 0x02 | sensor type(2) | total working time(4) | power-on time(4) | offset time(4) | Modbus RTU frame
/// Response payload: frame type(1) | status(1) | total working time(4) | power-on time(4) | offset time(4) | Modbus RTU frame
/// </summary>
public sealed class SolarmanV5Client(string host, uint loggerSerial, int port = 8899, TimeSpan? timeout = null)
    : IAsyncDisposable
{
    private const byte Start = 0xA5;
    private const byte End = 0x15;
    private const ushort RequestControlCode = 0x4510;
    private const ushort ResponseControlCode = 0x1510;
    private const int HeaderLength = 11;
    private const int RequestPayloadPrefixLength = 15;
    private const int ResponsePayloadPrefixLength = 14;

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _sequence = (ushort)Random.Shared.Next(0x0001, 0xFFFF);

    /// <summary>Optional hook for raw frame dumps (direction, bytes). Useful when probing unknown hardware.</summary>
    public Action<string, ReadOnlyMemory<byte>>? FrameTrace { get; set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_tcp is { Connected: true }) return;
        await DisposeConnectionAsync();

        _tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        await _tcp.ConnectAsync(host, port, cts.Token);
        _stream = _tcp.GetStream();
    }

    public async Task<ushort[]> ReadRegistersAsync(
        byte slaveId, byte function, ushort startAddress, ushort count, CancellationToken cancellationToken = default)
    {
        var request = ModbusRtu.BuildReadRequest(slaveId, function, startAddress, count);
        var response = await SendModbusAsync(request, cancellationToken);
        return ModbusRtu.ParseReadResponse(response, slaveId, function, count);
    }

    /// <summary>Sends a raw Modbus RTU frame (including CRC) and returns the raw Modbus RTU response.</summary>
    public async Task<byte[]> SendModbusAsync(byte[] modbusFrame, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await ConnectAsync(cancellationToken);
            var stream = _stream!;

            var sequence = unchecked(++_sequence);
            var request = BuildFrame(sequence, modbusFrame);
            FrameTrace?.Invoke("TX", request);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            await stream.WriteAsync(request, cts.Token);

            // The logger may interleave other frame types (e.g. heartbeats); skip until our response arrives.
            while (true)
            {
                var frame = await ReadFrameAsync(stream, cts.Token);
                FrameTrace?.Invoke("RX", frame);

                var control = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(3));
                if (control != ResponseControlCode) continue;
                if (frame[5] != (byte)sequence) continue; // stale response to an earlier request

                var modbusStart = HeaderLength + ResponsePayloadPrefixLength;
                var modbusLength = frame.Length - modbusStart - 2;
                if (modbusLength < 5)
                    throw new InvalidDataException(
                        "Logger answered but returned no Modbus data. The inverter may not have responded " +
                        "(wrong slave ID, unsupported register range, or the logger is not wired to the inverter's Modbus port).");

                return frame.AsSpan(modbusStart, modbusLength).ToArray();
            }
        }
        catch
        {
            // Connection state is unknown after a failure; reconnect on the next call.
            await DisposeConnectionAsync();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[] BuildFrame(ushort sequence, ReadOnlySpan<byte> modbusFrame)
    {
        var payloadLength = RequestPayloadPrefixLength + modbusFrame.Length;
        var frame = new byte[HeaderLength + payloadLength + 2];

        frame[0] = Start;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), RequestControlCode);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), loggerSerial);

        frame[HeaderLength] = 0x02; // frame type: solar inverter
        // sensor type, total working time, power-on time, offset time: all zero
        modbusFrame.CopyTo(frame.AsSpan(HeaderLength + RequestPayloadPrefixLength));

        frame[^2] = Checksum(frame);
        frame[^1] = End;
        return frame;
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new byte[3];
        await stream.ReadExactlyAsync(head, cancellationToken);
        if (head[0] != Start)
            throw new InvalidDataException($"Expected V5 start byte 0xA5, got 0x{head[0]:X2}. Is this a Solarman logger?");

        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(1));
        var frame = new byte[HeaderLength + payloadLength + 2];
        head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(3), cancellationToken);

        if (frame[^1] != End)
            throw new InvalidDataException($"Expected V5 end byte 0x15, got 0x{frame[^1]:X2}.");
        if (frame[^2] != Checksum(frame))
            throw new InvalidDataException("V5 frame checksum mismatch.");

        return frame;
    }

    /// <summary>Sum of all bytes between the start byte and the checksum byte, truncated to 8 bits.</summary>
    private static byte Checksum(ReadOnlySpan<byte> frame)
    {
        var sum = 0;
        foreach (var b in frame[1..^2]) sum += b;
        return (byte)sum;
    }

    private ValueTask DisposeConnectionAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync();
        _lock.Dispose();
    }
}

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace SolPnl.Plugin.Luxpower;

/// <summary>
/// Talks to an EG4 / Luxpower WiFi dongle on its local TCP port (8000), following lxp-bridge's implementation of
/// the dongle protocol. Every frame is little-endian:
///
///   0xA1 0x1A | protocol(2) | length(2) = bytes after this field | 0x01 | TCP function(1) | datalog serial(10) | data
///
/// "Translated data" (TCP function 0xC2) carries a Modbus-like request or reply:
///   request:  length(2) | address(1) | function(1) | inverter serial(10) | register(2) | count(2) | CRC-16/Modbus(2)
///   reply:    length(2) | address(1) | function(1) | inverter serial(10) | register(2) | byte count(1) | values | CRC
/// The CRC covers everything after the data length. Register values are little-endian. (Replies with protocol 1
/// carry a single value and no byte count.)
/// </summary>
public sealed class LuxpowerClient(string host, int port, string datalogSerial, string inverterSerial, TimeSpan? timeout = null)
    : IAsyncDisposable
{
    public const byte TcpHeartbeat = 0xC1;
    public const byte TcpTranslatedData = 0xC2;
    public const byte ReadHolding = 0x03;
    public const byte ReadInput = 0x04;

    /// <summary>lxp-bridge always reads 40 registers at a time.</summary>
    public const int MaxRegistersPerRead = 40;

    private const int HeaderLength = 18;

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public async Task<ushort[]> ReadRegistersAsync(byte function, ushort start, ushort count, CancellationToken cancellationToken = default)
    {
        if (count is 0 or > MaxRegistersPerRead)
            throw new ArgumentOutOfRangeException(nameof(count), $"Count must be 1..{MaxRegistersPerRead}.");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var stream = await ConnectAsync(cts.Token);
            await stream.WriteAsync(BuildReadRequest(datalogSerial, inverterSerial, function, start, count), cts.Token);

            // The dongle also sends heartbeats and the readings it collects for the cloud; skip (or answer) those
            // until the reply to this request arrives.
            while (true)
            {
                var frame = await ReadFrameAsync(stream, cts.Token);
                if (frame[7] == TcpHeartbeat)
                {
                    await stream.WriteAsync(frame, cts.Token); // echo, as lxp-bridge does, to keep the session alive
                    continue;
                }
                if (frame[7] != TcpTranslatedData) continue;
                if (ParseReadReply(frame) is { } reply && reply.Function == function && reply.Register == start &&
                    reply.Values.Length == count)
                    return reply.Values;
            }
        }
        catch
        {
            DisposeConnection(); // unknown state: reconnect next time
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static byte[] BuildReadRequest(string datalogSerial, string inverterSerial, byte function, ushort start, ushort count)
    {
        var data = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 18);
        data[2] = 0;                                   // address
        data[3] = function;
        WriteSerial(data.AsSpan(4), inverterSerial);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), start);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), count);

        var frame = new byte[HeaderLength + data.Length + 2];
        frame[0] = 0xA1;
        frame[1] = 0x1A;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 1);   // protocol 1 for reads
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)(frame.Length - 6));
        frame[6] = 1;
        frame[7] = TcpTranslatedData;
        WriteSerial(frame.AsSpan(8), datalogSerial);
        data.CopyTo(frame, HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), Crc16(data.AsSpan(2)));
        return frame;
    }

    public sealed record ReadReply(byte Function, string InverterSerial, ushort Register, ushort[] Values);

    /// <summary>Parses a translated-data read reply; null if it isn't one (or its checksum is wrong).</summary>
    public static ReadReply? ParseReadReply(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength + 16 + 2 || frame[7] != TcpTranslatedData) return null;
        var protocol = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]);
        var data = frame[HeaderLength..^2];
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]) != Crc16(data[2..])) return null;

        var function = data[3];
        if (function is not (ReadHolding or ReadInput)) return null;
        var serial = Encoding.ASCII.GetString(data.Slice(4, 10));
        var register = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);

        ReadOnlySpan<byte> bytes;
        if (protocol == 1)
            bytes = data[16..];
        else
        {
            if (data.Length < 17) return null;
            bytes = data[17..];
            if (bytes.Length != data[16]) return null;
        }
        if (bytes.Length % 2 != 0) return null;

        var values = new ushort[bytes.Length / 2];
        for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
        return new ReadReply(function, serial, register, values);
    }

    /// <summary>CRC-16/MODBUS (polynomial 0xA001 reflected, initial 0xFFFF).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    private static void WriteSerial(Span<byte> into, string serial)
    {
        if (serial.Length != 10) throw new ArgumentException("Serial numbers are exactly 10 characters.", nameof(serial));
        Encoding.ASCII.GetBytes(serial, into);
    }

    private async Task<NetworkStream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_tcp is { Connected: true } && _stream is not null) return _stream;
        DisposeConnection();
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, cancellationToken);
        return _stream = _tcp.GetStream();
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new byte[6];
        await stream.ReadExactlyAsync(head, cancellationToken);
        if (head[0] != 0xA1 || head[1] != 0x1A)
            throw new InvalidDataException($"Expected an EG4/Luxpower frame (A1 1A), got {head[0]:X2} {head[1]:X2}. Is this the dongle's port 8000?");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4));
        if (length is < 2 or > 1024) throw new InvalidDataException($"Invalid EG4/Luxpower frame length {length}.");
        var frame = new byte[6 + length];
        head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(6), cancellationToken);
        return frame;
    }

    private void DisposeConnection()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        DisposeConnection();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}

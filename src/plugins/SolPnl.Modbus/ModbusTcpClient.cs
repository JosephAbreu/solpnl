using System.Buffers.Binary;
using System.Net.Sockets;

namespace SolPnl.Modbus;

/// <summary>
/// Minimal Modbus TCP client for register reads (functions 0x03 / 0x04). Keeps one connection open and reconnects
/// after a failure; some devices (e.g. SolarEdge) accept only one Modbus TCP client at a time.
///
/// Frame layout (big-endian): transaction id(2) | protocol id 0(2) | length(2) | unit id(1) | PDU
/// Read request PDU:  function(1) | start address(2) | count(2)
/// Read response PDU: function(1) | byte count(1) | registers(2 × count), or function|0x80 | exception code(1)
/// </summary>
public sealed class ModbusTcpClient(string host, int port = 502, TimeSpan? timeout = null) : IAsyncDisposable
{
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;

    /// <summary>Modbus allows up to 125 registers per read.</summary>
    public const int MaxRegistersPerRead = 125;

    private const int HeaderLength = 7;

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transaction = (ushort)Random.Shared.Next(0, 0xFFFF);

    public async Task<ushort[]> ReadRegistersAsync(
        byte unitId, ushort startAddress, ushort count, byte function = ReadHoldingRegisters,
        CancellationToken cancellationToken = default)
    {
        if (count is 0 or > MaxRegistersPerRead)
            throw new ArgumentOutOfRangeException(nameof(count), $"Count must be 1..{MaxRegistersPerRead}.");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var stream = await ConnectAsync(cts.Token);

            var transaction = unchecked(++_transaction);
            var request = BuildReadRequest(transaction, unitId, function, startAddress, count);
            await stream.WriteAsync(request, cts.Token);

            // Skip responses to earlier requests that timed out (their transaction id doesn't match).
            while (true)
            {
                var (header, pdu) = await ReadFrameAsync(stream, cts.Token);
                if (BinaryPrimitives.ReadUInt16BigEndian(header) != transaction) continue;
                if (header[6] != unitId)
                    throw new InvalidDataException($"Response from unit {header[6]}, expected {unitId}.");
                return ParseReadResponse(pdu, function, count);
            }
        }
        catch (ModbusException)
        {
            throw; // the device answered; the connection is fine
        }
        catch
        {
            // Connection state is unknown after a failure; reconnect on the next call.
            DisposeConnection();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static byte[] BuildReadRequest(ushort transaction, byte unitId, byte function, ushort startAddress, ushort count)
    {
        var frame = new byte[HeaderLength + 5];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6); // unit id + 5-byte PDU
        frame[6] = unitId;
        frame[7] = function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), count);
        return frame;
    }

    public static ushort[] ParseReadResponse(ReadOnlySpan<byte> pdu, byte function, ushort count)
    {
        if (pdu.Length >= 2 && pdu[0] == (function | 0x80))
            throw new ModbusException(pdu[1]);
        if (pdu.Length < 2 || pdu[0] != function)
            throw new InvalidDataException($"Unexpected Modbus response (function 0x{(pdu.Length > 0 ? pdu[0] : 0):X2}).");

        var byteCount = pdu[1];
        if (byteCount != count * 2 || pdu.Length != 2 + byteCount)
            throw new InvalidDataException($"Unexpected byte count {byteCount} for {count} registers.");

        var registers = new ushort[count];
        for (var i = 0; i < count; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(2 + i * 2, 2));
        return registers;
    }

    private async Task<NetworkStream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_tcp is { Connected: true } && _stream is not null) return _stream;
        DisposeConnection();

        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, cancellationToken);
        return _stream = _tcp.GetStream();
    }

    private static async Task<(byte[] Header, byte[] Pdu)> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0)
            throw new InvalidDataException("Not a Modbus TCP response (protocol id isn't 0). Is this the device's Modbus TCP port?");

        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (length is < 2 or > 254)
            throw new InvalidDataException($"Invalid Modbus TCP length {length}.");

        var pdu = new byte[length - 1];
        await stream.ReadExactlyAsync(pdu, cancellationToken);
        return (header, pdu);
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

public sealed class ModbusException(byte exceptionCode)
    : Exception($"Modbus exception 0x{exceptionCode:X2}: {Describe(exceptionCode)}")
{
    public byte ExceptionCode { get; } = exceptionCode;

    private static string Describe(byte code) => code switch
    {
        0x01 => "illegal function",
        0x02 => "illegal data address",
        0x03 => "illegal data value",
        0x04 => "slave device failure",
        0x05 => "acknowledge",
        0x06 => "slave device busy",
        0x08 => "memory parity error",
        0x0A => "gateway path unavailable",
        0x0B => "gateway target device failed to respond",
        _ => "unknown",
    };
}

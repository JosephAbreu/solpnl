using System.Buffers.Binary;

namespace SolPnl.Plugin.SolarmanSrne.Protocol;

/// <summary>Minimal Modbus RTU framing for register reads (functions 0x03 / 0x04).</summary>
public static class ModbusRtu
{
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;

    /// <summary>Modbus allows up to 125 registers per read.</summary>
    public const int MaxRegistersPerRead = 125;

    public static byte[] BuildReadRequest(byte slaveId, byte function, ushort startAddress, ushort count)
    {
        if (count is 0 or > MaxRegistersPerRead)
            throw new ArgumentOutOfRangeException(nameof(count), $"Count must be 1..{MaxRegistersPerRead}.");

        var frame = new byte[8];
        frame[0] = slaveId;
        frame[1] = function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), count);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), Crc16(frame.AsSpan(0, 6)));
        return frame;
    }

    public static ushort[] ParseReadResponse(ReadOnlySpan<byte> frame, byte slaveId, byte function, ushort count)
    {
        if (frame.Length < 5)
            throw new InvalidDataException($"Modbus response too short ({frame.Length} bytes).");

        var expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        var actualCrc = Crc16(frame[..^2]);
        if (expectedCrc != actualCrc)
            throw new InvalidDataException($"Modbus CRC mismatch (got 0x{expectedCrc:X4}, computed 0x{actualCrc:X4}).");

        if (frame[0] != slaveId)
            throw new InvalidDataException($"Response from slave {frame[0]}, expected {slaveId}.");

        if (frame[1] == (function | 0x80))
            throw new ModbusException(frame[2]);

        if (frame[1] != function)
            throw new InvalidDataException($"Unexpected function code 0x{frame[1]:X2}.");

        var byteCount = frame[2];
        if (byteCount != count * 2 || frame.Length != 3 + byteCount + 2)
            throw new InvalidDataException($"Unexpected byte count {byteCount} for {count} registers.");

        var registers = new ushort[count];
        for (var i = 0; i < count; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3 + i * 2, 2));
        return registers;
    }

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
        0x06 => "slave device busy",
        _ => "unknown",
    };
}

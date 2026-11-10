using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SolPnl.Modbus;

namespace SolPnl.Plugin.SunSpec.Tests;

/// <summary>Builds a SunSpec register map ("SunS", then ID | length | data models, then the end marker).</summary>
internal sealed class SunSpecImage
{
    public Dictionary<ushort, ushort> Registers { get; } = new();
    private int _next;

    public SunSpecImage(ushort baseAddress = 40000)
    {
        Registers[baseAddress] = 0x5375;
        Registers[(ushort)(baseAddress + 1)] = 0x6E53;
        _next = baseAddress + 2;
    }

    public SunSpecImage Model(ushort id, ushort[] data)
    {
        Registers[(ushort)_next] = id;
        Registers[(ushort)(_next + 1)] = (ushort)data.Length;
        for (var i = 0; i < data.Length; i++) Registers[(ushort)(_next + 2 + i)] = data[i];
        _next += 2 + data.Length;
        return this;
    }

    public SunSpecImage End()
    {
        Registers[(ushort)_next] = 0xFFFF;
        Registers[(ushort)(_next + 1)] = 0;
        return this;
    }

    public SunSpecDiscovery.ReadRegisters Reader() => (start, count, _) =>
    {
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            if (!Registers.TryGetValue((ushort)(start + i), out result[i]))
                throw new ModbusException(0x02);
        }
        return Task.FromResult(result);
    };

    // ---- Model builders ------------------------------------------------------------------------------------------

    public static ushort[] Text(string s, int registers)
    {
        var r = new ushort[registers];
        for (var i = 0; i < s.Length && i / 2 < registers; i++)
            r[i / 2] |= (ushort)(i % 2 == 0 ? s[i] << 8 : s[i]);
        return r;
    }

    public static ushort[] Common(string manufacturer, string model, string version, string serial)
    {
        var r = new ushort[66];
        Text(manufacturer, 16).CopyTo(r, 0);
        Text(model, 16).CopyTo(r, 16);
        Text(version, 8).CopyTo(r, 40);
        Text(serial, 16).CopyTo(r, 48);
        r[64] = 1;
        return r;
    }

    public static ushort S(int v) => unchecked((ushort)(short)v);

    public static ushort[] Fill(int length, ushort value)
    {
        var r = new ushort[length];
        Array.Fill(r, value);
        return r;
    }

    public static (ushort High, ushort Low) Float(float v)
    {
        var bits = BitConverter.SingleToUInt32Bits(v);
        return ((ushort)(bits >> 16), (ushort)bits);
    }

    public static void PutFloat(ushort[] r, int offset, float v) => (r[offset], r[offset + 1]) = Float(v);
}

/// <summary>A fake Modbus TCP device serving one register map per unit ID.</summary>
internal sealed class FakeModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Dictionary<byte, Dictionary<ushort, ushort>> _units;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public int Port { get; }
    public int Requests;

    public FakeModbusServer(Dictionary<byte, Dictionary<ushort, ushort>> units)
    {
        _units = units;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        try
        {
            using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
            var stream = socket.GetStream();
            var header = new byte[7];
            while (!_stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
                var pdu = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
                await stream.ReadExactlyAsync(pdu, _stop.Token);
                Interlocked.Increment(ref Requests);

                var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                byte[] reply;
                if (_units.TryGetValue(header[6], out var regs) &&
                    Enumerable.Range(start, count).All(a => regs.ContainsKey((ushort)a)))
                {
                    reply = new byte[2 + count * 2];
                    reply[0] = pdu[0];
                    reply[1] = (byte)(count * 2);
                    for (var i = 0; i < count; i++)
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2 + i * 2), regs[(ushort)(start + i)]);
                }
                else reply = [(byte)(pdu[0] | 0x80), 0x02];

                var frame = new byte[7 + reply.Length];
                header.AsSpan(0, 4).CopyTo(frame);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(reply.Length + 1));
                frame[6] = header[6];
                reply.CopyTo(frame, 7);
                await stream.WriteAsync(frame, _stop.Token);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
    }
}

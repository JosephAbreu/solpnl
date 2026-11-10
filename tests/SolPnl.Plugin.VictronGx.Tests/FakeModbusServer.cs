using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SolPnl.Plugin.VictronGx.Tests;

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

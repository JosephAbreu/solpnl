using System.Globalization;
using SolPnl.Plugin.SolarmanSrne.Protocol;

// SolPnl probe: verifies local Modbus access through a Solarman V5 stick logger and dumps raw registers.
//
//   dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678
//   dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678 --range 0x0100:32 --verbose

var options = ParseArgs(args);
if (options is null)
{
    PrintUsage();
    return 1;
}

// Default ranges cover the blocks commonly used by SRNE-family hybrid inverters
// (device info, real-time battery/PV, inverter/grid/load). Unconfirmed for this model — that's what we're probing.
var ranges = options.Ranges.Count > 0
    ? options.Ranges
    : [(0x000A, 20), (0x0100, 32), (0x0200, 64)];

await using var client = new SolarmanV5Client(options.Host, options.Serial, options.Port, TimeSpan.FromSeconds(options.TimeoutSeconds));
if (options.Verbose)
    client.FrameTrace = (dir, bytes) => Console.WriteLine($"  {dir} {Convert.ToHexString(bytes.Span)}");

Console.WriteLine($"Connecting to {options.Host}:{options.Port} (logger serial {options.Serial}, slave {options.SlaveId}, function 0x{options.Function:X2})...");
try
{
    await client.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not connect: {ex.Message}");
    Console.Error.WriteLine("Check the IP address and that the logger accepts local connections on this port.");
    return 2;
}
Console.WriteLine("TCP connection established.\n");

var failures = 0;
foreach (var (start, count) in ranges)
{
    for (var offset = 0; offset < count; offset += ModbusRtu.MaxRegistersPerRead)
    {
        var chunkStart = (ushort)(start + offset);
        var chunkCount = (ushort)Math.Min(ModbusRtu.MaxRegistersPerRead, count - offset);
        Console.WriteLine($"Registers 0x{chunkStart:X4}..0x{chunkStart + chunkCount - 1:X4}:");
        try
        {
            var registers = await client.ReadRegistersAsync(options.SlaveId, options.Function, chunkStart, chunkCount);
            PrintHeader();
            for (var i = 0; i < registers.Length; i++)
                PrintRegister(chunkStart + i, registers[i]);
        }
        catch (ModbusException ex) when (chunkCount > 1 && options.Scan)
        {
            // SRNE-family inverters reject a block read if any address in it is undefined.
            // Fall back to one register at a time to map which addresses actually exist.
            Console.WriteLine($"  Block read failed ({ex.Message}); scanning registers individually...");
            PrintHeader();
            var valid = 0;
            for (var i = 0; i < chunkCount; i++)
            {
                var address = (ushort)(chunkStart + i);
                try
                {
                    var single = await client.ReadRegistersAsync(options.SlaveId, options.Function, address, 1);
                    PrintRegister(address, single[0]);
                    valid++;
                }
                catch (ModbusException single)
                {
                    Console.WriteLine($"  0x{address:X4}   ----  (exception 0x{single.ExceptionCode:X2})");
                }
            }
            if (valid == 0) failures++;
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"  FAILED: {ex.Message}");
        }
        Console.WriteLine();
    }
}

return failures == 0 ? 0 : 3;

static void PrintHeader() => Console.WriteLine("  addr     hex     uint16    int16");

static void PrintRegister(int address, ushort value) =>
    Console.WriteLine($"  0x{address:X4}   {value:X4}  {value,7}  {(short)value,7}");

static ProbeOptions? ParseArgs(string[] args)
{
    string? host = null;
    uint? serial = null;
    int port = 8899, timeout = 10;
    byte slave = 1, function = ModbusRtu.ReadHoldingRegisters;
    var verbose = false;
    var scan = true;
    var ranges = new List<(int Start, int Count)>();

    try
    {
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException($"Missing value for {args[i]}");
            switch (args[i])
            {
                case "--host": host = Next(); break;
                case "--serial": serial = (uint)ParseNumber(Next()); break;
                case "--port": port = (int)ParseNumber(Next()); break;
                case "--slave": slave = (byte)ParseNumber(Next()); break;
                case "--function": function = (byte)ParseNumber(Next()); break;
                case "--timeout": timeout = (int)ParseNumber(Next()); break;
                case "--verbose" or "-v": verbose = true; break;
                case "--no-scan": scan = false; break;
                case "--range":
                    var parts = Next().Split(':');
                    if (parts.Length != 2) throw new FormatException("--range must be START:COUNT, e.g. 0x0100:32");
                    ranges.Add(((int)ParseNumber(parts[0]), (int)ParseNumber(parts[1])));
                    break;
                case "--help" or "-h": return null;
                default: throw new FormatException($"Unknown argument {args[i]}");
            }
        }
    }
    catch (Exception ex) when (ex is FormatException or OverflowException)
    {
        Console.Error.WriteLine(ex.Message);
        return null;
    }

    return host is null || serial is null
        ? null
        : new ProbeOptions(host, serial.Value, port, slave, function, timeout, verbose, scan, ranges);
}

static long ParseNumber(string text) =>
    text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : long.Parse(text, CultureInfo.InvariantCulture);

static void PrintUsage() => Console.WriteLine("""
    Usage: SolPnl.Probe --host <ip> --serial <logger serial> [options]

      --host <ip>          Logger IP address on your LAN
      --serial <number>    Logger serial number (from the sticker on the stick, decimal)
      --port <n>           Logger TCP port (default 8899)
      --slave <n>          Inverter Modbus slave ID (default 1)
      --function <n>       3 = holding registers (default), 4 = input registers
      --range <start:cnt>  Register range to read, repeatable (e.g. 0x0100:32)
      --timeout <sec>      Per-request timeout (default 10)
      --verbose, -v        Print raw V5 frames
      --no-scan            Do not retry failed block reads one register at a time
    """);

internal sealed record ProbeOptions(
    string Host, uint Serial, int Port, byte SlaveId, byte Function, int TimeoutSeconds, bool Verbose, bool Scan,
    List<(int Start, int Count)> Ranges);

using SolPnl.Modbus;

namespace SolPnl.Plugin.SunSpec;

/// <summary>A SunSpec model found on a device: its ID, the address of its first data register, and its length.</summary>
public sealed record SunSpecModel(ushort Id, ushort Start, ushort Length);

/// <summary>
/// Finds a device's SunSpec models: the "SunS" marker at one of the standard base addresses, followed by a chain of
/// models, each "ID | length | data", ending with ID 0xFFFF.
/// </summary>
public static class SunSpecDiscovery
{
    /// <summary>Where SunSpec says the map may start, in the order to try.</summary>
    public static readonly IReadOnlyList<ushort> BaseAddresses = [40000, 0, 50000];

    private const ushort MarkerHigh = 0x5375; // "Su"
    private const ushort MarkerLow = 0x6E53;  // "nS"
    private const ushort EndId = 0xFFFF;

    /// <summary>Stops a corrupt chain from being walked forever.</summary>
    private const int MaxModels = 64;

    /// <summary>Reads (start, count) holding registers.</summary>
    public delegate Task<ushort[]> ReadRegisters(ushort start, ushort count, CancellationToken cancellationToken);

    public static async Task<IReadOnlyList<SunSpecModel>> DiscoverAsync(ReadRegisters read, CancellationToken cancellationToken = default)
    {
        foreach (var baseAddress in BaseAddresses)
        {
            ushort[] marker;
            try
            {
                marker = await read(baseAddress, 2, cancellationToken);
            }
            catch (ModbusException)
            {
                continue; // address doesn't exist on this device
            }
            if (marker is not [MarkerHigh, MarkerLow]) continue;

            var models = new List<SunSpecModel>();
            var address = baseAddress + 2;
            while (models.Count < MaxModels && address + 2 <= ushort.MaxValue)
            {
                var head = await read((ushort)address, 2, cancellationToken);
                if (head[0] == EndId || head[0] == 0) break;
                var start = address + 2;
                if (start + head[1] > ushort.MaxValue + 1)
                    throw new InvalidDataException($"SunSpec model {head[0]} at {address} runs past the end of the address space.");
                models.Add(new SunSpecModel(head[0], (ushort)start, head[1]));
                address = start + head[1];
            }
            return models;
        }

        throw new InvalidDataException(
            "No SunSpec data found (looked for the \"SunS\" marker at registers 40000, 0 and 50000). Check that Modbus TCP " +
            "with SunSpec is turned on in the inverter's settings and that the Modbus address (unit ID) is right.");
    }

    /// <summary>Reads a model's data registers, in reads of at most 125 registers.</summary>
    public static async Task<ushort[]> ReadModelAsync(ReadRegisters read, SunSpecModel model, CancellationToken cancellationToken = default)
    {
        var data = new ushort[model.Length];
        for (var offset = 0; offset < model.Length; offset += ModbusTcpClient.MaxRegistersPerRead)
        {
            var count = (ushort)Math.Min(ModbusTcpClient.MaxRegistersPerRead, model.Length - offset);
            var part = await read((ushort)(model.Start + offset), count, cancellationToken);
            part.CopyTo(data, offset);
        }
        return data;
    }
}

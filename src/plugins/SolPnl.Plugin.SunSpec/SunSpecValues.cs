using System.Text;

namespace SolPnl.Plugin.SunSpec;

/// <summary>
/// Reads SunSpec point types from a model's registers (offset 0 = the first register after the model's ID and
/// length). Every reader returns null for SunSpec's "not implemented" value, so callers skip those points.
/// </summary>
internal static class SunSpecValues
{
    public static double? U16(ushort[] r, int offset, int? sf = 0) =>
        offset < r.Length && r[offset] != 0xFFFF && sf is { } s ? Scale(r[offset], s) : null;

    public static double? S16(ushort[] r, int offset, int? sf = 0) =>
        offset < r.Length && r[offset] != 0x8000 && sf is { } s ? Scale((short)r[offset], s) : null;

    /// <summary>A scale factor (sunssf): the value is multiplied by 10^sf. Valid range -10..10.</summary>
    public static int? Sf(ushort[] r, int offset) =>
        offset < r.Length && (short)r[offset] is var s and >= -10 and <= 10 ? s : null;

    /// <summary>Enum or bit field (16 bit).</summary>
    public static int? Enum16(ushort[] r, int offset) =>
        offset < r.Length && r[offset] != 0xFFFF ? r[offset] : null;

    /// <summary>Bit field (32 bit, high word first); 0xFFFFFFFF is "not implemented".</summary>
    public static uint? Bits32(ushort[] r, int offset) =>
        offset + 1 < r.Length && (uint)(r[offset] << 16 | r[offset + 1]) is var v && v != 0xFFFFFFFF ? v : null;

    /// <summary>Accumulator (32 bit, high word first); 0 is "not implemented".</summary>
    public static double? Acc32(ushort[] r, int offset, int? sf = 0) =>
        offset + 1 < r.Length && (uint)(r[offset] << 16 | r[offset + 1]) is var v && v != 0 && sf is { } s ? Scale(v, s) : null;

    /// <summary>IEEE 754 float (32 bit, high word first); NaN is "not implemented".</summary>
    public static double? F32(ushort[] r, int offset)
    {
        if (offset + 1 >= r.Length) return null;
        var v = BitConverter.UInt32BitsToSingle((uint)(r[offset] << 16 | r[offset + 1]));
        return float.IsFinite(v) ? v : null;
    }

    /// <summary>ASCII string, two characters per register, high byte first, padded with NULs or spaces.</summary>
    public static string? Text(ushort[] r, int offset, int registers)
    {
        var sb = new StringBuilder();
        for (var i = offset; i < offset + registers && i < r.Length; i++)
        {
            foreach (var b in new[] { (byte)(r[i] >> 8), (byte)r[i] })
            {
                if (b == 0) goto done;
                if (b is >= 0x20 and <= 0x7E) sb.Append((char)b);
            }
        }
        done:
        var text = sb.ToString().Trim();
        return text.Length > 0 ? text : null;
    }

    private static double Scale(double value, int sf) => sf == 0 ? value : value * Math.Pow(10, sf);
}

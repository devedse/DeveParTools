using System.Runtime.CompilerServices;

namespace DeveParTools.Core.Hashing;

/// <summary>
/// CRC-32 (IEEE 802.3, as used by PAR2 and zip). Bulk computation uses the vectorized
/// <see cref="System.IO.Hashing.Crc32"/>; this class adds raw-register helpers and zero extension.
/// </summary>
public static class Crc32Ieee
{
    private const uint ReflectedPolynomial = 0xEDB88320u;
    internal static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ ReflectedPolynomial : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    /// <summary>Continue a CRC (start with 0), zlib style.</summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        if (crc == 0)
        {
            return System.IO.Hashing.Crc32.HashToUInt32(data);
        }

        // System.IO.Hashing cannot be seeded; fall back to the table for continuation.
        return ~UpdateRaw(~crc, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint UpdateRawByte(uint reg, byte b) => (reg >> 8) ^ Table[(reg ^ b) & 0xFF];

    internal static uint UpdateRaw(uint reg, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            reg = (reg >> 8) ^ Table[(reg ^ b) & 0xFF];
        }

        return reg;
    }

    /// <summary>CRC of the data followed by <paramref name="count"/> zero bytes, given the CRC of the data.</summary>
    public static uint AppendZeros(uint crc, long count)
    {
        uint reg = ~crc;
        while (count-- > 0)
        {
            reg = (reg >> 8) ^ Table[reg & 0xFF];
        }

        return ~reg;
    }
}

/// <summary>Rolling CRC-32 over a fixed window (PAR2 uses the slice size as window).</summary>
public sealed class RollingCrc32
{
    private readonly uint[] _outTable = new uint[256];
    private readonly uint _windowMask;

    public int Window { get; }

    public RollingCrc32(int window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        Window = window;
        _windowMask = Crc32Ieee.AppendZeros(0, window);
        // The CRC register is linear: compute the 8 single-bit contributions, combine for all 256 bytes.
        Span<uint> bits = stackalloc uint[8];
        for (int bit = 0; bit < 8; bit++)
        {
            uint reg = Crc32Ieee.UpdateRawByte(0, (byte)(1 << bit));
            for (int i = 0; i < window; i++)
            {
                reg = (reg >> 8) ^ Crc32Ieee.Table[reg & 0xFF];
            }

            bits[bit] = reg;
        }

        for (int b = 0; b < 256; b++)
        {
            uint v = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) != 0)
                {
                    v ^= bits[bit];
                }
            }

            _outTable[b] = v;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Slide(uint crc, byte outgoing, byte incoming)
    {
        uint raw = crc ^ _windowMask;
        raw = Crc32Ieee.UpdateRawByte(raw, incoming) ^ _outTable[outgoing];
        return raw ^ _windowMask;
    }
}

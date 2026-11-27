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

    /// <summary>Continues a CRC: pass the previous result, or 0 to start (zlib style).</summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        // System.IO.Hashing is vectorized (PCLMULQDQ / ARM CRC32) but cannot start from a given CRC, so hash the new
        // data on its own and combine.
        uint fresh = System.IO.Hashing.Crc32.HashToUInt32(data);
        return crc == 0 ? fresh : Combine(crc, fresh, data.Length);
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

    /// <summary>CRC of the data followed by <paramref name="count"/> zero bytes, given the CRC of the data. O(log count).</summary>
    public static uint AppendZeros(uint crc, long count) => ~MultiplyModP(XPow8N(count), ~crc);

    /// <summary>
    /// CRC of A followed by B from CRC(A), CRC(B) and |B|: <c>CRC(A‖B) = CRC(A)·x^(8|B|) ⊕ CRC(B)</c> (mod P).
    /// The derivation is in <see cref="Crc64Iso.Combine"/>; only the width differs.
    /// </summary>
    public static uint Combine(uint crcA, uint crcB, long lengthB) => MultiplyModP(XPow8N(lengthB), crcA) ^ crcB;

    /// <summary>x^(8·2^k) mod P for k = 0..63.</summary>
    private static readonly uint[] X8Pow2K = BuildPowers();

    private static uint[] BuildPowers()
    {
        var t = new uint[64];
        t[0] = 1u << (31 - 8); // x^8
        for (int k = 1; k < 64; k++)
        {
            t[k] = MultiplyModP(t[k - 1], t[k - 1]);
        }

        return t;
    }

    /// <summary>x^(8n) mod P by square-and-multiply.</summary>
    internal static uint XPow8N(long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        uint p = 1u << 31; // x^0
        for (int k = 0; n != 0; k++, n >>= 1)
        {
            if ((n & 1) != 0)
            {
                p = MultiplyModP(X8Pow2K[k], p);
            }
        }

        return p;
    }

    /// <summary>a·b mod P for reflected polynomials (bit 31 = x^0); see <see cref="Crc64Iso.MultiplyModP"/>.</summary>
    internal static uint MultiplyModP(uint a, uint b)
    {
        uint product = 0;
        for (uint m = 1u << 31; m != 0; m >>= 1)
        {
            if ((a & m) != 0)
            {
                product ^= b;
            }

            b = (b & 1) != 0 ? (b >> 1) ^ ReflectedPolynomial : b >> 1;
        }

        return product;
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

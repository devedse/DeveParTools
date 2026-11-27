using System.Runtime.CompilerServices;

namespace DeveParTools.Core.Hashing;

/// <summary>
/// CRC-64-ISO (polynomial x^64 + x^4 + x^3 + x + 1, reflected, init and xor-out all ones), as used by PAR3 as
/// its rolling hash. Check value of "123456789" is 0xB90956C775A41001.
/// </summary>
public static class Crc64Iso
{
    public const ulong ReflectedPolynomial = 0xD800000000000000UL;

    // Slicing-by-8 tables.
    private static readonly ulong[][] Tables = BuildTables();

    private static ulong[][] BuildTables()
    {
        var t = new ulong[8][];
        for (int k = 0; k < 8; k++)
        {
            t[k] = new ulong[256];
        }

        for (int i = 0; i < 256; i++)
        {
            ulong c = (ulong)i;
            for (int j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ ReflectedPolynomial : c >> 1;
            }

            t[0][i] = c;
        }

        for (int i = 0; i < 256; i++)
        {
            for (int k = 1; k < 8; k++)
            {
                ulong prev = t[k - 1][i];
                t[k][i] = (prev >> 8) ^ t[0][prev & 0xFF];
            }
        }

        return t;
    }

    /// <summary>Continue a CRC: pass the previous result (or 0 to start) like zlib's crc32().</summary>
    public static ulong Compute(ReadOnlySpan<byte> data, ulong crc = 0) => ~UpdateRaw(~crc, data);

    /// <summary>Raw register update (no init/xor-out handling).</summary>
    public static ulong UpdateRaw(ulong reg, ReadOnlySpan<byte> data)
    {
        var t0 = Tables[0];
        var t1 = Tables[1];
        var t2 = Tables[2];
        var t3 = Tables[3];
        var t4 = Tables[4];
        var t5 = Tables[5];
        var t6 = Tables[6];
        var t7 = Tables[7];
        int i = 0;
        while (i + 8 <= data.Length)
        {
            reg ^= Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in data[i]));
            reg = t7[reg & 0xFF] ^ t6[(reg >> 8) & 0xFF] ^ t5[(reg >> 16) & 0xFF] ^ t4[(reg >> 24) & 0xFF]
                ^ t3[(reg >> 32) & 0xFF] ^ t2[(reg >> 40) & 0xFF] ^ t1[(reg >> 48) & 0xFF] ^ t0[reg >> 56];
            i += 8;
        }

        for (; i < data.Length; i++)
        {
            reg = (reg >> 8) ^ t0[(reg ^ data[i]) & 0xFF];
        }

        return reg;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong UpdateRawByte(ulong reg, byte b) => (reg >> 8) ^ Tables[0][(reg ^ b) & 0xFF];

    /// <summary>CRC of <paramref name="count"/> zero bytes appended to a running CRC.</summary>
    public static ulong AppendZeros(ulong crc, long count)
    {
        ulong reg = ~crc;
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        while (count > 0)
        {
            int n = (int)Math.Min(count, zeros.Length);
            reg = UpdateRaw(reg, zeros[..n]);
            count -= n;
        }

        return ~reg;
    }
}

/// <summary>Rolling CRC-64-ISO over a fixed window: slide one byte at a time in O(1).</summary>
public sealed class RollingCrc64
{
    private readonly ulong[] _outTable = new ulong[256];
    private readonly ulong _windowMask;

    public int Window { get; }

    public RollingCrc64(int window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        Window = window;
        // CRC(window) = Raw(window) ^ CRC(zeros(window)) since Raw(zeros) == 0.
        _windowMask = Crc64Iso.AppendZeros(0, window);
        // Contribution of a byte that sits 'window' bytes before the end, after one more byte is appended.
        // The CRC register is linear: compute the 8 single-bit contributions, combine for all 256 bytes.
        Span<ulong> bits = stackalloc ulong[8];
        var zeros = new byte[Math.Min(window, 1 << 16)];
        for (int bit = 0; bit < 8; bit++)
        {
            ulong reg = Crc64Iso.UpdateRawByte(0, (byte)(1 << bit));
            long left = window;
            while (left > 0)
            {
                int n = (int)Math.Min(left, zeros.Length);
                reg = Crc64Iso.UpdateRaw(reg, zeros.AsSpan(0, n));
                left -= n;
            }

            bits[bit] = reg;
        }

        for (int b = 0; b < 256; b++)
        {
            ulong v = 0;
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

    /// <summary>Given CRC of window [i, i+W), returns CRC of [i+1, i+W+1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Slide(ulong crc, byte outgoing, byte incoming)
    {
        ulong raw = crc ^ _windowMask;
        raw = Crc64Iso.UpdateRawByte(raw, incoming) ^ _outTable[outgoing];
        return raw ^ _windowMask;
    }
}

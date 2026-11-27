namespace DeveParTools.Core.Galois;

/// <summary>An element of a large field (more than 16 bytes), stored little-endian.</summary>
public readonly struct GfBigElement : IEquatable<GfBigElement>
{
    private readonly byte[]? _bytes;

    public GfBigElement(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    internal byte[] Raw => _bytes ?? [];

    public bool Equals(GfBigElement other) => Bytes.SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is GfBigElement e && Equals(e);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Bytes);
        return h.ToHashCode();
    }

    public static bool operator ==(GfBigElement a, GfBigElement b) => a.Equals(b);

    public static bool operator !=(GfBigElement a, GfBigElement b) => !a.Equals(b);
}

/// <summary>
/// GF(2^(8n)) for any n (PAR3 requires every byte-multiple field). Slow bit-serial element arithmetic, split-table
/// region arithmetic. Intended for completeness; the 8/16-bit fields are the fast paths.
/// </summary>
public sealed class GfBigField : IGaloisField<GfBigElement>
{
    private readonly byte[] _poly;

    public GfBigField(int symbolSize, ReadOnlySpan<byte> generatorWithoutLeadingOne)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolSize, 1);
        if (generatorWithoutLeadingOne.Length != symbolSize)
        {
            throw new ArgumentException("Generator must have exactly SymbolSize bytes.", nameof(generatorWithoutLeadingOne));
        }

        SymbolSize = symbolSize;
        _poly = generatorWithoutLeadingOne.ToArray();
        GeneratorBytes = _poly;
    }

    public int SymbolSize { get; }

    public byte[] GeneratorBytes { get; }

    public GfBigElement Zero => new(new byte[SymbolSize]);

    public GfBigElement One
    {
        get
        {
            var b = new byte[SymbolSize];
            b[0] = 1;
            return new GfBigElement(b);
        }
    }

    public GfBigElement Add(GfBigElement a, GfBigElement b)
    {
        var r = new byte[SymbolSize];
        for (int i = 0; i < SymbolSize; i++)
        {
            r[i] = (byte)(a.Raw[i] ^ b.Raw[i]);
        }

        return new GfBigElement(r);
    }

    public GfBigElement Mul(GfBigElement a, GfBigElement b)
    {
        var r = new byte[SymbolSize];
        var aa = (byte[])a.Raw.Clone();
        var bb = b.Raw;
        int bits = SymbolSize * 8;
        for (int bit = 0; bit < bits; bit++)
        {
            if ((bb[bit >> 3] & (1 << (bit & 7))) != 0)
            {
                for (int i = 0; i < SymbolSize; i++)
                {
                    r[i] ^= aa[i];
                }
            }

            // aa <<= 1 with reduction
            bool carry = (aa[SymbolSize - 1] & 0x80) != 0;
            for (int i = SymbolSize - 1; i > 0; i--)
            {
                aa[i] = (byte)((aa[i] << 1) | (aa[i - 1] >> 7));
            }

            aa[0] = (byte)(aa[0] << 1);
            if (carry)
            {
                for (int i = 0; i < SymbolSize; i++)
                {
                    aa[i] ^= _poly[i];
                }
            }
        }

        return new GfBigElement(r);
    }

    public GfBigElement Inv(GfBigElement a)
    {
        if (IsZero(a))
        {
            throw new DivideByZeroException("Zero has no inverse.");
        }

        var result = One;
        var sq = a;
        int bits = SymbolSize * 8;
        for (int i = 0; i < bits; i++)
        {
            if (i != 0)
            {
                result = Mul(result, sq);
            }

            sq = Mul(sq, sq);
        }

        return result;
    }

    public bool IsZero(GfBigElement a)
    {
        foreach (var b in a.Bytes)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    public GfBigElement FromUInt64(ulong value)
    {
        var r = new byte[SymbolSize];
        for (int i = 0; i < Math.Min(8, SymbolSize); i++)
        {
            r[i] = (byte)(value >> (8 * i));
        }

        return new GfBigElement(r);
    }

    public GfBigElement Read(ReadOnlySpan<byte> bytes) => new(bytes[..SymbolSize].ToArray());

    public void Write(GfBigElement value, Span<byte> bytes) => value.Bytes.CopyTo(bytes);

    public void RowMulAdd(Span<GfBigElement> dst, ReadOnlySpan<GfBigElement> src, GfBigElement c)
    {
        if (IsZero(c))
        {
            return;
        }

        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = Add(dst[i], Mul(c, src[i]));
        }
    }

    public void RowScale(Span<GfBigElement> row, GfBigElement c)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Mul(c, row[i]);
        }
    }

    public IRegionTables CreateTables(ReadOnlySpan<GfBigElement> coefficients)
    {
        int n = SymbolSize;
        var t = new SplitTables(coefficients.Length, n);
        for (int k = 0; k < coefficients.Length; k++)
        {
            for (int p = 0; p < n; p++)
            {
                for (int v = 1; v < 256; v++)
                {
                    var x = new byte[n];
                    x[p] = (byte)v;
                    var prod = Mul(coefficients[k], new GfBigElement(x));
                    prod.Bytes.CopyTo(t.Data.AsSpan(((k * n + p) * 256 + v) * n, n));
                }
            }
        }

        return t;
    }

    public unsafe void MulAddMulti(IRegionTables tables, int first, int count, byte** sources, byte* destination, nint length, bool overwrite)
    {
        var t = (SplitTables)tables;
        int n = SymbolSize;
        var acc = new byte[n];
        for (nint pos = 0; pos + n <= length; pos += n)
        {
            if (overwrite)
            {
                Array.Clear(acc);
            }
            else
            {
                new ReadOnlySpan<byte>(destination + pos, n).CopyTo(acc);
            }

            for (int f = 0; f < count; f++)
            {
                byte* s = sources[f] + pos;
                for (int p = 0; p < n; p++)
                {
                    int off = (((first + f) * n + p) * 256 + s[p]) * n;
                    for (int i = 0; i < n; i++)
                    {
                        acc[i] ^= t.Data[off + i];
                    }
                }
            }

            acc.CopyTo(new Span<byte>(destination + pos, n));
        }
    }

    private sealed class SplitTables(int count, int symbolSize) : IRegionTables
    {
        public byte[] Data { get; } = new byte[Math.Max(1, count * symbolSize * 256 * symbolSize)];

        public int Count { get; } = count;

        public void Dispose()
        {
        }
    }
}

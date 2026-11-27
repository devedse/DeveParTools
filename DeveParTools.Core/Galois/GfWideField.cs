namespace DeveParTools.Core.Galois;

/// <summary>
/// Generic GF(2^(8n)) for 1 ≤ n ≤ 16 bytes with any irreducible generator, using <see cref="UInt128"/> elements.
/// Element multiplication is carry-less with reduction; region operations use per-byte split tables
/// (n × 256 entries per coefficient), so any width runs at table-lookup speed.
/// </summary>
public sealed class GfWideField : IGaloisField<UInt128>
{
    private readonly int _bits;
    private readonly UInt128 _mask;
    private readonly UInt128 _poly; // generator without leading x^bits term
    private readonly UInt128 _top;

    public GfWideField(int symbolSize, ReadOnlySpan<byte> generatorWithoutLeadingOne)
    {
        if (symbolSize is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolSize), "GfWideField supports 1..16 byte symbols.");
        }

        if (generatorWithoutLeadingOne.Length != symbolSize)
        {
            throw new ArgumentException("Generator must have exactly SymbolSize bytes.", nameof(generatorWithoutLeadingOne));
        }

        SymbolSize = symbolSize;
        _bits = symbolSize * 8;
        _mask = _bits == 128 ? UInt128.MaxValue : (UInt128.One << _bits) - 1;
        _top = UInt128.One << (_bits - 1);
        GeneratorBytes = generatorWithoutLeadingOne.ToArray();
        _poly = Read(generatorWithoutLeadingOne);
    }

    public int SymbolSize { get; }

    public byte[] GeneratorBytes { get; }

    public UInt128 Zero => UInt128.Zero;

    public UInt128 One => UInt128.One;

    public UInt128 Add(UInt128 a, UInt128 b) => a ^ b;

    public UInt128 Mul(UInt128 a, UInt128 b)
    {
        UInt128 r = UInt128.Zero;
        while (b != UInt128.Zero)
        {
            if ((b & UInt128.One) != UInt128.Zero)
            {
                r ^= a;
            }

            b >>= 1;
            bool carry = (a & _top) != UInt128.Zero;
            a = (a << 1) & _mask;
            if (carry)
            {
                a ^= _poly;
            }
        }

        return r;
    }

    public UInt128 Inv(UInt128 a)
    {
        if (a == UInt128.Zero)
        {
            throw new DivideByZeroException("Zero has no inverse.");
        }

        // a^(2^bits - 2) by square-and-multiply: exponent bits are all ones except the lowest.
        UInt128 result = UInt128.One;
        UInt128 sq = a;
        for (int i = 0; i < _bits; i++)
        {
            if (i != 0)
            {
                result = Mul(result, sq);
            }

            sq = Mul(sq, sq);
        }

        return result;
    }

    public bool IsZero(UInt128 a) => a == UInt128.Zero;

    public UInt128 FromUInt64(ulong value) => (UInt128)value & _mask;

    public UInt128 Read(ReadOnlySpan<byte> bytes)
    {
        UInt128 v = UInt128.Zero;
        for (int i = SymbolSize - 1; i >= 0; i--)
        {
            v = (v << 8) | bytes[i];
        }

        return v;
    }

    public void Write(UInt128 value, Span<byte> bytes)
    {
        for (int i = 0; i < SymbolSize; i++)
        {
            bytes[i] = (byte)value;
            value >>= 8;
        }
    }

    public void RowMulAdd(Span<UInt128> dst, ReadOnlySpan<UInt128> src, UInt128 c)
    {
        if (c == UInt128.Zero)
        {
            return;
        }

        for (int i = 0; i < dst.Length; i++)
        {
            if (src[i] != UInt128.Zero)
            {
                dst[i] ^= Mul(c, src[i]);
            }
        }
    }

    public void RowScale(Span<UInt128> row, UInt128 c)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Mul(c, row[i]);
        }
    }

    public IRegionTables CreateTables(ReadOnlySpan<UInt128> coefficients)
    {
        var t = new SplitTables(coefficients.Length, SymbolSize);
        for (int k = 0; k < coefficients.Length; k++)
        {
            var c = coefficients[k];
            int baseIdx = k * SymbolSize * 256;
            for (int p = 0; p < SymbolSize; p++)
            {
                for (int v = 1; v < 256; v++)
                {
                    t.Data[baseIdx + p * 256 + v] = Mul(c, (UInt128)v << (8 * p));
                }
            }
        }

        return t;
    }

    public unsafe void MulAddMulti(IRegionTables tables, int first, int count, byte** sources, byte* destination, nint length, bool overwrite)
    {
        var t = (SplitTables)tables;
        int n = SymbolSize;
        Span<byte> tmp = stackalloc byte[16];
        for (nint pos = 0; pos + n <= length; pos += n)
        {
            UInt128 acc = overwrite ? UInt128.Zero : Read(new ReadOnlySpan<byte>(destination + pos, n));
            for (int f = 0; f < count; f++)
            {
                byte* s = sources[f] + pos;
                int baseIdx = (first + f) * n * 256;
                for (int p = 0; p < n; p++)
                {
                    acc ^= t.Data[baseIdx + p * 256 + s[p]];
                }
            }

            Write(acc, new Span<byte>(destination + pos, n));
        }
    }

    private sealed class SplitTables(int count, int symbolSize) : IRegionTables
    {
        public UInt128[] Data { get; } = new UInt128[Math.Max(1, count * symbolSize * 256)];

        public int Count { get; } = count;

        public void Dispose()
        {
        }
    }
}

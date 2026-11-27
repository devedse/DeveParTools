using System.Runtime.InteropServices;

namespace DeveParTools.Core.Galois;

/// <summary>Pre-computed per-coefficient tables for region multiply-add.</summary>
public interface IRegionTables : IDisposable
{
    int Count { get; }
}

/// <summary>
/// A Galois field GF(2^(8·SymbolSize)) with element type <typeparamref name="T"/> and fast region arithmetic on
/// little-endian symbol arrays.
/// </summary>
public interface IGaloisField<T> where T : IEquatable<T>
{
    /// <summary>Bytes per element in data buffers.</summary>
    int SymbolSize { get; }

    /// <summary>The generator (reduction) polynomial without the leading term, little-endian, <see cref="SymbolSize"/> bytes.</summary>
    byte[] GeneratorBytes { get; }

    T Zero { get; }

    T One { get; }

    T Add(T a, T b);

    T Mul(T a, T b);

    T Inv(T a);

    T Div(T a, T b) => Mul(a, Inv(b));

    bool IsZero(T a);

    /// <summary>Element whose little-endian byte representation is <paramref name="value"/> (truncated to the field).</summary>
    T FromUInt64(ulong value);

    /// <summary>Read an element from <see cref="SymbolSize"/> little-endian bytes.</summary>
    T Read(ReadOnlySpan<byte> bytes);

    /// <summary>Write an element as <see cref="SymbolSize"/> little-endian bytes.</summary>
    void Write(T value, Span<byte> bytes);

    /// <summary><c>dst[i] ^= c * src[i]</c> on element arrays (used for matrix row operations).</summary>
    void RowMulAdd(Span<T> dst, ReadOnlySpan<T> src, T c);

    /// <summary><c>row[i] = c * row[i]</c>.</summary>
    void RowScale(Span<T> row, T c);

    IRegionTables CreateTables(ReadOnlySpan<T> coefficients);

    /// <summary><c>dst (^)= Σ coef[first+i]·src[i]</c> over byte regions of <paramref name="length"/> bytes (multiple of SymbolSize).</summary>
    unsafe void MulAddMulti(IRegionTables tables, int first, int count, byte** sources, byte* destination, nint length, bool overwrite);
}

public sealed class Gf8Field : IGaloisField<byte>
{
    public Gf8Field(GaloisField8 field)
    {
        Inner = field;
        GeneratorBytes = [(byte)field.Polynomial];
    }

    public GaloisField8 Inner { get; }

    public int SymbolSize => 1;

    public byte[] GeneratorBytes { get; }

    public byte Zero => 0;

    public byte One => 1;

    public byte Add(byte a, byte b) => (byte)(a ^ b);

    public byte Mul(byte a, byte b) => Inner.Mul(a, b);

    public byte Inv(byte a) => Inner.Inv(a);

    public bool IsZero(byte a) => a == 0;

    public byte FromUInt64(ulong value) => (byte)value;

    public byte Read(ReadOnlySpan<byte> bytes) => bytes[0];

    public void Write(byte value, Span<byte> bytes) => bytes[0] = value;

    public void RowMulAdd(Span<byte> dst, ReadOnlySpan<byte> src, byte c) => Gf8Kernels.MulAdd(Inner, c, src, dst);

    public void RowScale(Span<byte> row, byte c) => Gf8Kernels.MulAdd(Inner, c, row, row, overwrite: true);

    public IRegionTables CreateTables(ReadOnlySpan<byte> coefficients) => new Tables(Gf8MulTables.Create(Inner, coefficients));

    public unsafe void MulAddMulti(IRegionTables tables, int first, int count, byte** sources, byte* destination, nint length, bool overwrite)
        => Gf8Kernels.MulAddMulti(((Tables)tables).Inner, first, count, sources, destination, length, overwrite);

    private sealed class Tables(Gf8MulTables inner) : IRegionTables
    {
        public Gf8MulTables Inner { get; } = inner;

        public int Count => Inner.Count;

        public void Dispose() => Inner.Dispose();
    }
}

public sealed class Gf16Field : IGaloisField<ushort>
{
    public Gf16Field(GaloisField16 field)
    {
        Inner = field;
        GeneratorBytes = [(byte)field.Polynomial, (byte)(field.Polynomial >> 8)];
    }

    public GaloisField16 Inner { get; }

    public int SymbolSize => 2;

    public byte[] GeneratorBytes { get; }

    public ushort Zero => 0;

    public ushort One => 1;

    public ushort Add(ushort a, ushort b) => (ushort)(a ^ b);

    public ushort Mul(ushort a, ushort b) => Inner.Mul(a, b);

    public ushort Inv(ushort a) => Inner.Inv(a);

    public bool IsZero(ushort a) => a == 0;

    public ushort FromUInt64(ulong value) => (ushort)value;

    public ushort Read(ReadOnlySpan<byte> bytes) => (ushort)(bytes[0] | (bytes[1] << 8));

    public void Write(ushort value, Span<byte> bytes)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
    }

    public void RowMulAdd(Span<ushort> dst, ReadOnlySpan<ushort> src, ushort c)
        => Gf16Kernels.MulAdd(Inner, c, MemoryMarshal.AsBytes(src), MemoryMarshal.AsBytes(dst));

    public void RowScale(Span<ushort> row, ushort c)
    {
        var bytes = MemoryMarshal.AsBytes(row);
        Gf16Kernels.MulAdd(Inner, c, bytes, bytes, overwrite: true);
    }

    public IRegionTables CreateTables(ReadOnlySpan<ushort> coefficients) => new Tables(Gf16MulTables.Create(Inner, coefficients));

    public unsafe void MulAddMulti(IRegionTables tables, int first, int count, byte** sources, byte* destination, nint length, bool overwrite)
        => Gf16Kernels.MulAddMulti(((Tables)tables).Inner, first, count, sources, destination, length, overwrite);

    private sealed class Tables(Gf16MulTables inner) : IRegionTables
    {
        public Gf16MulTables Inner { get; } = inner;

        public int Count => Inner.Count;

        public void Dispose() => Inner.Dispose();
    }
}

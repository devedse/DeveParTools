using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DeveParTools.Core.Galois;

/// <summary>
/// Pre-computed multiplication tables for a list of GF(2^16) coefficients, in the layout a given
/// <see cref="SimdLevel"/> kernel expects. Owns native memory.
/// </summary>
public sealed unsafe class Gf16MulTables : IDisposable
{
    internal byte* Ptr;
    public int Count { get; }
    public int Stride { get; }
    public SimdLevel Level { get; }
    public IGf16Multiplier Field { get; }

    /// <summary>The tables of the 64 coefficients u·16^m (see <see cref="Gf16Kernels.Basis"/>); kept alive for <see cref="_basisPtr"/>.</summary>
    private readonly byte[] _basis;
    private readonly byte* _basisPtr;

    private Gf16MulTables(IGf16Multiplier field, int count, SimdLevel level)
    {
        Field = field;
        Count = count;
        Level = level;
        Stride = Gf16Kernels.TableStride(level);
        Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, count * Stride), 64);
        _basis = Gf16Kernels.Basis(field, level);
        _basisPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_basis));
    }

    public static Gf16MulTables Create(IGf16Multiplier field, ReadOnlySpan<ushort> coefficients, SimdLevel? level = null)
    {
        var t = new Gf16MulTables(field, coefficients.Length, level ?? Gf16Kernels.Level);
        t.Set(0, coefficients);
        return t;
    }

    /// <summary>Replace the coefficient at <paramref name="index"/>.</summary>
    public void Set(int index, ushort coefficient) => Set(index, [coefficient]);

    /// <summary>
    /// Replace the coefficients from <paramref name="first"/> on with <paramref name="coefficients"/>. Cheap (about
    /// 20 CPU cycles per coefficient with AVX2): a table is the XOR of four rows of the field's basis tables.
    /// </summary>
    public void Set(int first, ReadOnlySpan<ushort> coefficients)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(coefficients.Length, Count - first, nameof(coefficients));
        for (int k = 0; k < coefficients.Length; k++)
        {
            Gf16Kernels.TableFromBasis(_basisPtr, Stride, coefficients[k], Ptr + (nint)(first + k) * Stride);
        }
    }

    public void Dispose()
    {
        if (Ptr != null)
        {
            NativeMemory.AlignedFree(Ptr);
            Ptr = null;
        }

        GC.SuppressFinalize(this);
    }

    ~Gf16MulTables() => Dispose();
}

/// <summary>
/// SIMD kernels for GF(2^16) region arithmetic on little-endian 16-bit symbols:
/// <c>dst ^= c0*src0 ^ c1*src1 ^ ...</c>. Lengths must be a multiple of 2 bytes.
/// </summary>
public static unsafe class Gf16Kernels
{
    private static SimdLevel _level = SimdSupport.Best;

    /// <summary>The kernel used by default. Can be overridden (e.g. for benchmarks), must be supported.</summary>
    public static SimdLevel Level
    {
        get => _level;
        set
        {
            if (!SimdSupport.IsSupported(value))
            {
                throw new PlatformNotSupportedException($"SIMD level {value} is not supported on this CPU.");
            }

            _level = value;
        }
    }

    internal static int TableStride(SimdLevel level) => level switch
    {
        SimdLevel.Scalar => 1024,
        SimdLevel.Ssse3 or SimdLevel.Neon => 128,
        SimdLevel.Avx2 => 256,
        SimdLevel.Avx512 => 512,
        SimdLevel.Gfni128 => 64,
        SimdLevel.Gfni256 => 128,
        SimdLevel.Gfni512 => 256,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>The 4 GFNI affine matrices (lo→lo, hi→lo, lo→hi, hi→hi) for multiplying by <paramref name="c"/>.</summary>
    public static void GetAffineMatrices(IGf16Multiplier field, ushort c, out ulong loToLo, out ulong hiToLo, out ulong loToHi, out ulong hiToHi)
    {
        loToLo = hiToLo = loToHi = hiToHi = 0;
        for (int i = 0; i < 8; i++)
        {
            ulong rLL = 0, rHL = 0, rLH = 0, rHH = 0;
            for (int j = 0; j < 8; j++)
            {
                int pl = field.Mul(c, (ushort)(1 << j));
                int ph = field.Mul(c, (ushort)(1 << (j + 8)));
                rLL |= (ulong)((pl >> i) & 1) << j;
                rHL |= (ulong)((ph >> i) & 1) << j;
                rLH |= (ulong)((pl >> (i + 8)) & 1) << j;
                rHH |= (ulong)((ph >> (i + 8)) & 1) << j;
            }

            int shift = 8 * (7 - i);
            loToLo |= rLL << shift;
            hiToLo |= rHL << shift;
            loToHi |= rLH << shift;
            hiToHi |= rHH << shift;
        }
    }

    /// <summary>Scalar emulation of GF2P8AFFINEQB for one byte (used for testing the matrices).</summary>
    public static byte AffineEmulate(byte x, ulong matrix)
    {
        int r = 0;
        for (int i = 0; i < 8; i++)
        {
            byte row = (byte)(matrix >> (8 * (7 - i)));
            r |= (System.Numerics.BitOperations.PopCount((uint)(row & x)) & 1) << i;
        }

        return (byte)r;
    }

    private static readonly ConditionalWeakTable<IGf16Multiplier, byte[]?[]> Bases = new();

    /// <summary>
    /// The tables (in <paramref name="level"/>'s layout) of the 64 coefficients u·16^m, u = 0..15, m = 0..3, row
    /// 16·m + u, in pinned memory. Built once per field and level (64 tables the slow way), they make every other
    /// table cheap: see <see cref="Gf16MulTables.Set(int, ReadOnlySpan{ushort})"/>.
    /// </summary>
    internal static byte[] Basis(IGf16Multiplier field, SimdLevel level)
    {
        var perLevel = Bases.GetValue(field, static _ => new byte[]?[Enum.GetValues<SimdLevel>().Length]);
        return perLevel[(int)level] ??= BuildBasis(field, level);
    }

    /// <summary>
    /// The table of <paramref name="c"/> from the basis: multiplying by c is linear in c, and so is every entry of
    /// every table layout, so table(c) is the XOR of the tables of c's four nibbles, each in its place (u·16^m).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void TableFromBasis(byte* basis, int stride, int c, byte* dst)
    {
        byte* r0 = basis + (c & 15) * stride;
        byte* r1 = basis + (16 + ((c >> 4) & 15)) * stride;
        byte* r2 = basis + (32 + ((c >> 8) & 15)) * stride;
        byte* r3 = basis + (48 + ((c >> 12) & 15)) * stride;
        for (int i = 0; i < stride; i += Vector<byte>.Count)
        {
            var v = Unsafe.ReadUnaligned<Vector<byte>>(r0 + i) ^ Unsafe.ReadUnaligned<Vector<byte>>(r1 + i)
                  ^ Unsafe.ReadUnaligned<Vector<byte>>(r2 + i) ^ Unsafe.ReadUnaligned<Vector<byte>>(r3 + i);
            Unsafe.WriteUnaligned(dst + i, v);
        }
    }

    private static byte[] BuildBasis(IGf16Multiplier field, SimdLevel level)
    {
        int stride = TableStride(level);
        var basis = GC.AllocateArray<byte>(64 * stride, pinned: true);
        fixed (byte* p = basis)
        {
            for (int row = 0; row < 64; row++)
            {
                BuildTable(field, (ushort)((row & 15) << (4 * (row >> 4))), level, p + row * stride);
            }
        }

        return basis;
    }

    internal static void BuildTable(IGf16Multiplier field, ushort c, SimdLevel level, byte* dst)
    {
        switch (level)
        {
            case SimdLevel.Scalar:
            {
                var t = (ushort*)dst;
                for (int b = 0; b < 256; b++)
                {
                    t[b] = field.Mul(c, (ushort)b);
                    t[256 + b] = field.Mul(c, (ushort)(b << 8));
                }

                break;
            }

            case SimdLevel.Ssse3:
            case SimdLevel.Neon:
            case SimdLevel.Avx2:
            case SimdLevel.Avx512:
            {
                int width = level switch { SimdLevel.Avx2 => 32, SimdLevel.Avx512 => 64, _ => 16 };
                // 8 tables: for nibble n (0..3): products' low byte, then high byte.
                for (int n = 0; n < 4; n++)
                {
                    for (int v = 0; v < 16; v++)
                    {
                        ushort p = field.Mul(c, (ushort)(v << (4 * n)));
                        for (int rep = 0; rep < width; rep += 16)
                        {
                            dst[(n * 2) * width + rep + v] = (byte)p;
                            dst[(n * 2 + 1) * width + rep + v] = (byte)(p >> 8);
                        }
                    }
                }

                break;
            }

            case SimdLevel.Gfni128:
            case SimdLevel.Gfni256:
            case SimdLevel.Gfni512:
            {
                int width = level switch { SimdLevel.Gfni256 => 32, SimdLevel.Gfni512 => 64, _ => 16 };
                GetAffineMatrices(field, c, out var ll, out var hl, out var lh, out var hh);
                ulong[] m = [ll, hl, lh, hh];
                for (int k = 0; k < 4; k++)
                {
                    for (int rep = 0; rep < width; rep += 8)
                    {
                        *(ulong*)(dst + k * width + rep) = m[k];
                    }
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(level));
        }
    }

    /// <summary><c>dst ^= c * src</c> (or <c>dst = c * src</c> when <paramref name="overwrite"/>).</summary>
    public static void MulAdd(IGf16Multiplier field, ushort c, ReadOnlySpan<byte> src, Span<byte> dst, bool overwrite = false)
    {
        if (src.Length != dst.Length)
        {
            throw new ArgumentException("Source and destination must have the same length.");
        }

        if ((dst.Length & 1) != 0)
        {
            throw new ArgumentException("Length must be a multiple of 2 bytes for GF(2^16).");
        }

        if (c == 0)
        {
            if (overwrite)
            {
                dst.Clear();
            }

            return;
        }

        var level = _level;
        byte* table = stackalloc byte[1024];
        fixed (byte* basis = Basis(field, level))
        {
            TableFromBasis(basis, TableStride(level), c, table);
        }

        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            byte* sp = s;
            MulAddMultiCore(level, d, &sp, table, TableStride(level), 1, dst.Length, overwrite);
        }
    }

    /// <summary>
    /// <c>dst (^)= sum over i of coef[first+i] * srcs[i]</c> for <paramref name="count"/> sources.
    /// Each source must point to at least <paramref name="length"/> readable bytes.
    /// </summary>
    public static void MulAddMulti(Gf16MulTables tables, int first, int count, byte** srcs, byte* dst, nint length, bool overwrite = false)
    {
        if ((length & 1) != 0)
        {
            throw new ArgumentException("Length must be a multiple of 2 bytes for GF(2^16).");
        }

        if (count <= 0)
        {
            if (overwrite)
            {
                new Span<byte>(dst, checked((int)length)).Clear();
            }

            return;
        }

        MulAddMultiCore(tables.Level, dst, srcs, tables.Ptr + (nint)first * tables.Stride, tables.Stride, count, length, overwrite);
    }

    /// <summary>
    /// <c>dst (^)= Σ coefficients[i]·srcs[i]</c> with the sources and the destination in the prepared layout of
    /// <paramref name="level"/>'s kernel (see <see cref="Prepare"/>), <paramref name="length"/> a multiple of
    /// <see cref="PreparedBlock"/>. One source at a time: its tables (the XOR of four rows of the basis, see
    /// <see cref="TableFromBasis"/>) go straight into registers, and the destination, a chunk that stays in the L1
    /// cache, is updated once per source. That beats a list of tables in memory, from which a multi-source kernel
    /// loads 8 vectors per source for every 64 bytes; ParPar's "shuffle" kernels work this way too.
    /// </summary>
    public static void MulAddPrepared(IGf16Multiplier field, SimdLevel level, ReadOnlySpan<ushort> coefficients, byte** srcs, byte* dst, nint length, bool overwrite = false)
    {
        fixed (byte* basis = Basis(field, level))
        {
            MulAddPrepared(basis, level, coefficients, srcs, dst, length, overwrite);
        }
    }

    internal static void MulAddPrepared(byte* basis, SimdLevel level, ReadOnlySpan<ushort> coefficients, byte** srcs, byte* dst, nint length, bool overwrite)
    {
        CheckPrepared(level, length);
        if (coefficients.Length == 0)
        {
            if (overwrite)
            {
                new Span<byte>(dst, checked((int)length)).Clear();
            }

            return;
        }

        int stride = TableStride(level);
        if (level == SimdLevel.Scalar)
        {
            // The scalar kernel's layout is the normal one; it adds up to 16 sources per pass over the destination.
            byte* tables = stackalloc byte[16 * 1024];
            for (int g = 0; g < coefficients.Length; g += 16)
            {
                int n = Math.Min(16, coefficients.Length - g);
                for (int k = 0; k < n; k++)
                {
                    TableFromBasis(basis, stride, coefficients[g + k], tables + k * stride);
                }

                ScalarTail(level, dst, srcs + g, tables, stride, n, 0, length, overwrite && g == 0);
            }

            return;
        }

        for (int i = 0; i < coefficients.Length; i++)
        {
            int c = coefficients[i];
            byte* r0 = basis + (c & 15) * stride;
            byte* r1 = basis + (16 + ((c >> 4) & 15)) * stride;
            byte* r2 = basis + (32 + ((c >> 8) & 15)) * stride;
            byte* r3 = basis + (48 + ((c >> 12) & 15)) * stride;
            byte* src = srcs[i];
            bool set = overwrite && i == 0;
            switch (level)
            {
                case SimdLevel.Avx2:
                    Avx2Single(r0, r1, r2, r3, src, dst, length, set);
                    break;
                case SimdLevel.Avx512:
                    Avx512Single(r0, r1, r2, r3, src, dst, length, set);
                    break;
                case SimdLevel.Ssse3:
                    Ssse3Single(r0, r1, r2, r3, src, dst, length, set);
                    break;
                case SimdLevel.Neon:
                    NeonSingle(r0, r1, r2, r3, src, dst, length, set);
                    break;
                case SimdLevel.Gfni128:
                    Gfni128Single(r0, r1, r2, r3, src, dst, length, set);
                    break;
                case SimdLevel.Gfni256:
                    Gfni256Single(r0, r1, r2, r3, src, dst, length, set);
                    break;
                default: // Gfni512 (CheckPrepared has rejected unknown levels)
                    Gfni512Single(r0, r1, r2, r3, src, dst, length, set);
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Row128(byte* r0, byte* r1, byte* r2, byte* r3, int offset)
        => Vector128.Load(r0 + offset) ^ Vector128.Load(r1 + offset) ^ Vector128.Load(r2 + offset) ^ Vector128.Load(r3 + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Row256(byte* r0, byte* r1, byte* r2, byte* r3, int offset)
        => Vector256.Load(r0 + offset) ^ Vector256.Load(r1 + offset) ^ Vector256.Load(r2 + offset) ^ Vector256.Load(r3 + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<byte> Row512(byte* r0, byte* r1, byte* r2, byte* r3, int offset)
        => Vector512.Load(r0 + offset) ^ Vector512.Load(r1 + offset) ^ Vector512.Load(r2 + offset) ^ Vector512.Load(r3 + offset);

    private static void Avx2Single(byte* r0, byte* r1, byte* r2, byte* r3, byte* src, byte* dst, nint length, bool overwrite)
    {
        // The nibble tables (see BuildTable): nibble n's low bytes at 64·n, its high bytes at 64·n + 32.
        var l0 = Row256(r0, r1, r2, r3, 0);
        var h0 = Row256(r0, r1, r2, r3, 32);
        var l1 = Row256(r0, r1, r2, r3, 64);
        var h1 = Row256(r0, r1, r2, r3, 96);
        var l2 = Row256(r0, r1, r2, r3, 128);
        var h2 = Row256(r0, r1, r2, r3, 160);
        var l3 = Row256(r0, r1, r2, r3, 192);
        var h3 = Row256(r0, r1, r2, r3, 224);
        var m0f = Vector256.Create((byte)0x0F);
        for (nint pos = 0; pos < length; pos += 64)
        {
            var lo = Avx.LoadVector256(src + pos);
            var hi = Avx.LoadVector256(src + pos + 32);
            var n0 = lo & m0f;
            var n1 = Avx2.ShiftRightLogical(lo.AsUInt16(), 4).AsByte() & m0f;
            var n2 = hi & m0f;
            var n3 = Avx2.ShiftRightLogical(hi.AsUInt16(), 4).AsByte() & m0f;
            var pl = Avx2.Shuffle(l0, n0) ^ Avx2.Shuffle(l1, n1) ^ Avx2.Shuffle(l2, n2) ^ Avx2.Shuffle(l3, n3);
            var ph = Avx2.Shuffle(h0, n0) ^ Avx2.Shuffle(h1, n1) ^ Avx2.Shuffle(h2, n2) ^ Avx2.Shuffle(h3, n3);
            if (!overwrite)
            {
                pl ^= Avx.LoadVector256(dst + pos);
                ph ^= Avx.LoadVector256(dst + pos + 32);
            }

            Avx.Store(dst + pos, pl);
            Avx.Store(dst + pos + 32, ph);
        }
    }

    private static void Avx512Single(byte* r0, byte* r1, byte* r2, byte* r3, byte* src, byte* dst, nint length, bool overwrite)
    {
        var l0 = Row512(r0, r1, r2, r3, 0);
        var h0 = Row512(r0, r1, r2, r3, 64);
        var l1 = Row512(r0, r1, r2, r3, 128);
        var h1 = Row512(r0, r1, r2, r3, 192);
        var l2 = Row512(r0, r1, r2, r3, 256);
        var h2 = Row512(r0, r1, r2, r3, 320);
        var l3 = Row512(r0, r1, r2, r3, 384);
        var h3 = Row512(r0, r1, r2, r3, 448);
        var m0f = Vector512.Create((byte)0x0F);
        for (nint pos = 0; pos < length; pos += 128)
        {
            var lo = Vector512.Load(src + pos);
            var hi = Vector512.Load(src + pos + 64);
            var n0 = lo & m0f;
            var n1 = Avx512BW.ShiftRightLogical(lo.AsUInt16(), 4).AsByte() & m0f;
            var n2 = hi & m0f;
            var n3 = Avx512BW.ShiftRightLogical(hi.AsUInt16(), 4).AsByte() & m0f;
            var pl = Avx512BW.Shuffle(l0, n0) ^ Avx512BW.Shuffle(l1, n1) ^ Avx512BW.Shuffle(l2, n2) ^ Avx512BW.Shuffle(l3, n3);
            var ph = Avx512BW.Shuffle(h0, n0) ^ Avx512BW.Shuffle(h1, n1) ^ Avx512BW.Shuffle(h2, n2) ^ Avx512BW.Shuffle(h3, n3);
            if (!overwrite)
            {
                pl ^= Vector512.Load(dst + pos);
                ph ^= Vector512.Load(dst + pos + 64);
            }

            pl.Store(dst + pos);
            ph.Store(dst + pos + 64);
        }
    }

    private static void Ssse3Single(byte* r0, byte* r1, byte* r2, byte* r3, byte* src, byte* dst, nint length, bool overwrite)
    {
        var l0 = Row128(r0, r1, r2, r3, 0);
        var h0 = Row128(r0, r1, r2, r3, 16);
        var l1 = Row128(r0, r1, r2, r3, 32);
        var h1 = Row128(r0, r1, r2, r3, 48);
        var l2 = Row128(r0, r1, r2, r3, 64);
        var h2 = Row128(r0, r1, r2, r3, 80);
        var l3 = Row128(r0, r1, r2, r3, 96);
        var h3 = Row128(r0, r1, r2, r3, 112);
        var m0f = Vector128.Create((byte)0x0F);
        for (nint pos = 0; pos < length; pos += 32)
        {
            var lo = Sse2.LoadVector128(src + pos);
            var hi = Sse2.LoadVector128(src + pos + 16);
            var n0 = lo & m0f;
            var n1 = Sse2.ShiftRightLogical(lo.AsUInt16(), 4).AsByte() & m0f;
            var n2 = hi & m0f;
            var n3 = Sse2.ShiftRightLogical(hi.AsUInt16(), 4).AsByte() & m0f;
            var pl = Ssse3.Shuffle(l0, n0) ^ Ssse3.Shuffle(l1, n1) ^ Ssse3.Shuffle(l2, n2) ^ Ssse3.Shuffle(l3, n3);
            var ph = Ssse3.Shuffle(h0, n0) ^ Ssse3.Shuffle(h1, n1) ^ Ssse3.Shuffle(h2, n2) ^ Ssse3.Shuffle(h3, n3);
            if (!overwrite)
            {
                pl ^= Sse2.LoadVector128(dst + pos);
                ph ^= Sse2.LoadVector128(dst + pos + 16);
            }

            Sse2.Store(dst + pos, pl);
            Sse2.Store(dst + pos + 16, ph);
        }
    }

    private static void NeonSingle(byte* r0, byte* r1, byte* r2, byte* r3, byte* src, byte* dst, nint length, bool overwrite)
    {
        var l0 = Row128(r0, r1, r2, r3, 0);
        var h0 = Row128(r0, r1, r2, r3, 16);
        var l1 = Row128(r0, r1, r2, r3, 32);
        var h1 = Row128(r0, r1, r2, r3, 48);
        var l2 = Row128(r0, r1, r2, r3, 64);
        var h2 = Row128(r0, r1, r2, r3, 80);
        var l3 = Row128(r0, r1, r2, r3, 96);
        var h3 = Row128(r0, r1, r2, r3, 112);
        var m0f = Vector128.Create((byte)0x0F);
        for (nint pos = 0; pos < length; pos += 32)
        {
            var lo = AdvSimd.LoadVector128(src + pos);
            var hi = AdvSimd.LoadVector128(src + pos + 16);
            var n0 = lo & m0f;
            var n1 = AdvSimd.ShiftRightLogical(lo, 4);
            var n2 = hi & m0f;
            var n3 = AdvSimd.ShiftRightLogical(hi, 4);
            var pl = AdvSimd.Arm64.VectorTableLookup(l0, n0) ^ AdvSimd.Arm64.VectorTableLookup(l1, n1)
                   ^ AdvSimd.Arm64.VectorTableLookup(l2, n2) ^ AdvSimd.Arm64.VectorTableLookup(l3, n3);
            var ph = AdvSimd.Arm64.VectorTableLookup(h0, n0) ^ AdvSimd.Arm64.VectorTableLookup(h1, n1)
                   ^ AdvSimd.Arm64.VectorTableLookup(h2, n2) ^ AdvSimd.Arm64.VectorTableLookup(h3, n3);
            if (!overwrite)
            {
                pl ^= AdvSimd.LoadVector128(dst + pos);
                ph ^= AdvSimd.LoadVector128(dst + pos + 16);
            }

            AdvSimd.Store(dst + pos, pl);
            AdvSimd.Store(dst + pos + 16, ph);
        }
    }

    private static void Gfni128Single(byte* r0, byte* r1, byte* r2, byte* r3, byte* src, byte* dst, nint length, bool overwrite)
    {
        // The affine matrices (see BuildTable): lo→lo, hi→lo, lo→hi, hi→hi.
        var ll = Row128(r0, r1, r2, r3, 0);
        var hl = Row128(r0, r1, r2, r3, 16);
        var lh = Row128(r0, r1, r2, r3, 32);
        var hh = Row128(r0, r1, r2, r3, 48);
        for (nint pos = 0; pos < length; pos += 32)
        {
            var lo = Sse2.LoadVector128(src + pos);
            var hi = Sse2.LoadVector128(src + pos + 16);
            var pl = Gfni.GaloisFieldAffineTransform(lo, ll, 0) ^ Gfni.GaloisFieldAffineTransform(hi, hl, 0);
            var ph = Gfni.GaloisFieldAffineTransform(lo, lh, 0) ^ Gfni.GaloisFieldAffineTransform(hi, hh, 0);
            if (!overwrite)
            {
                pl ^= Sse2.LoadVector128(dst + pos);
                ph ^= Sse2.LoadVector128(dst + pos + 16);
            }

            Sse2.Store(dst + pos, pl);
            Sse2.Store(dst + pos + 16, ph);
        }
    }

    private static void Gfni256Single(byte* r0, byte* r1, byte* r2, byte* r3, byte* src, byte* dst, nint length, bool overwrite)
    {
        var ll = Row256(r0, r1, r2, r3, 0);
        var hl = Row256(r0, r1, r2, r3, 32);
        var lh = Row256(r0, r1, r2, r3, 64);
        var hh = Row256(r0, r1, r2, r3, 96);
        for (nint pos = 0; pos < length; pos += 64)
        {
            var lo = Avx.LoadVector256(src + pos);
            var hi = Avx.LoadVector256(src + pos + 32);
            var pl = Gfni.V256.GaloisFieldAffineTransform(lo, ll, 0) ^ Gfni.V256.GaloisFieldAffineTransform(hi, hl, 0);
            var ph = Gfni.V256.GaloisFieldAffineTransform(lo, lh, 0) ^ Gfni.V256.GaloisFieldAffineTransform(hi, hh, 0);
            if (!overwrite)
            {
                pl ^= Avx.LoadVector256(dst + pos);
                ph ^= Avx.LoadVector256(dst + pos + 32);
            }

            Avx.Store(dst + pos, pl);
            Avx.Store(dst + pos + 32, ph);
        }
    }

    private static void Gfni512Single(byte* r0, byte* r1, byte* r2, byte* r3, byte* src, byte* dst, nint length, bool overwrite)
    {
        var ll = Row512(r0, r1, r2, r3, 0);
        var hl = Row512(r0, r1, r2, r3, 64);
        var lh = Row512(r0, r1, r2, r3, 128);
        var hh = Row512(r0, r1, r2, r3, 192);
        for (nint pos = 0; pos < length; pos += 128)
        {
            var lo = Vector512.Load(src + pos);
            var hi = Vector512.Load(src + pos + 64);
            var pl = Gfni.V512.GaloisFieldAffineTransform(lo, ll, 0) ^ Gfni.V512.GaloisFieldAffineTransform(hi, hl, 0);
            var ph = Gfni.V512.GaloisFieldAffineTransform(lo, lh, 0) ^ Gfni.V512.GaloisFieldAffineTransform(hi, hh, 0);
            if (!overwrite)
            {
                pl ^= Vector512.Load(dst + pos);
                ph ^= Vector512.Load(dst + pos + 64);
            }

            pl.Store(dst + pos);
            ph.Store(dst + pos + 64);
        }
    }

    /// <summary>
    /// The block of the prepared layout of <paramref name="level"/>'s kernel: 2·W bytes for vectors of W bytes (2 for
    /// the scalar kernel, whose layout is the normal one).
    /// </summary>
    public static int PreparedBlock(SimdLevel level) => level switch
    {
        SimdLevel.Scalar => 2,
        SimdLevel.Ssse3 or SimdLevel.Neon or SimdLevel.Gfni128 => 32,
        SimdLevel.Avx2 or SimdLevel.Gfni256 => 64,
        SimdLevel.Avx512 or SimdLevel.Gfni512 => 128,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>
    /// Converts a region to the prepared layout of <paramref name="level"/>'s kernel, in place: each block of 2·W bytes
    /// (W symbols, see <see cref="PreparedBlock"/>) becomes the W low bytes of its symbols, then the W high bytes,
    /// split exactly as the kernel splits them. The kernel works on each byte position on its own, so it can
    /// take data split once (an input that is multiplied into every output) instead of splitting it on every call.
    /// </summary>
    public static void Prepare(SimdLevel level, byte* region, nint length)
    {
        CheckPrepared(level, length);
        switch (level)
        {
            case SimdLevel.Ssse3:
            case SimdLevel.Gfni128:
                for (nint p = 0; p < length; p += 32)
                {
                    var a = Sse2.LoadVector128(region + p).AsUInt16();
                    var b = Sse2.LoadVector128(region + p + 16).AsUInt16();
                    var mff = Vector128.Create((ushort)0x00FF);
                    Sse2.Store(region + p, Sse2.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16()));
                    Sse2.Store(region + p + 16, Sse2.PackUnsignedSaturate(Sse2.ShiftRightLogical(a, 8).AsInt16(), Sse2.ShiftRightLogical(b, 8).AsInt16()));
                }

                break;
            case SimdLevel.Neon:
                for (nint p = 0; p < length; p += 32)
                {
                    var a = AdvSimd.LoadVector128(region + p);
                    var b = AdvSimd.LoadVector128(region + p + 16);
                    AdvSimd.Store(region + p, AdvSimd.Arm64.UnzipEven(a, b));
                    AdvSimd.Store(region + p + 16, AdvSimd.Arm64.UnzipOdd(a, b));
                }

                break;
            case SimdLevel.Avx2:
            case SimdLevel.Gfni256:
                for (nint p = 0; p < length; p += 64)
                {
                    var a = Avx.LoadVector256(region + p).AsUInt16();
                    var b = Avx.LoadVector256(region + p + 32).AsUInt16();
                    var mff = Vector256.Create((ushort)0x00FF);
                    Avx.Store(region + p, Avx2.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16()));
                    Avx.Store(region + p + 32, Avx2.PackUnsignedSaturate(Avx2.ShiftRightLogical(a, 8).AsInt16(), Avx2.ShiftRightLogical(b, 8).AsInt16()));
                }

                break;
            case SimdLevel.Avx512:
            case SimdLevel.Gfni512:
                for (nint p = 0; p < length; p += 128)
                {
                    var a = Vector512.Load(region + p).AsUInt16();
                    var b = Vector512.Load(region + p + 64).AsUInt16();
                    var mff = Vector512.Create((ushort)0x00FF);
                    Avx512BW.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16()).Store(region + p);
                    Avx512BW.PackUnsignedSaturate(Avx512BW.ShiftRightLogical(a, 8).AsInt16(), Avx512BW.ShiftRightLogical(b, 8).AsInt16()).Store(region + p + 64);
                }

                break;
        }
    }

    /// <summary>The inverse of <see cref="Prepare"/>: back to the normal layout (merged as the kernel merges its result).</summary>
    public static void Finish(SimdLevel level, byte* region, nint length)
    {
        CheckPrepared(level, length);
        switch (level)
        {
            case SimdLevel.Ssse3:
            case SimdLevel.Gfni128:
                for (nint p = 0; p < length; p += 32)
                {
                    var lo = Sse2.LoadVector128(region + p);
                    var hi = Sse2.LoadVector128(region + p + 16);
                    Sse2.Store(region + p, Sse2.UnpackLow(lo, hi));
                    Sse2.Store(region + p + 16, Sse2.UnpackHigh(lo, hi));
                }

                break;
            case SimdLevel.Neon:
                for (nint p = 0; p < length; p += 32)
                {
                    var lo = AdvSimd.LoadVector128(region + p);
                    var hi = AdvSimd.LoadVector128(region + p + 16);
                    AdvSimd.Store(region + p, AdvSimd.Arm64.ZipLow(lo, hi));
                    AdvSimd.Store(region + p + 16, AdvSimd.Arm64.ZipHigh(lo, hi));
                }

                break;
            case SimdLevel.Avx2:
            case SimdLevel.Gfni256:
                for (nint p = 0; p < length; p += 64)
                {
                    var lo = Avx.LoadVector256(region + p);
                    var hi = Avx.LoadVector256(region + p + 32);
                    Avx.Store(region + p, Avx2.UnpackLow(lo, hi));
                    Avx.Store(region + p + 32, Avx2.UnpackHigh(lo, hi));
                }

                break;
            case SimdLevel.Avx512:
            case SimdLevel.Gfni512:
                for (nint p = 0; p < length; p += 128)
                {
                    var lo = Vector512.Load(region + p);
                    var hi = Vector512.Load(region + p + 64);
                    Avx512BW.UnpackLow(lo, hi).Store(region + p);
                    Avx512BW.UnpackHigh(lo, hi).Store(region + p + 64);
                }

                break;
        }
    }

    private static void CheckPrepared(SimdLevel level, nint length)
    {
        if (length % PreparedBlock(level) != 0)
        {
            throw new ArgumentException($"Length must be a multiple of {PreparedBlock(level)} bytes for the prepared layout of {level}.", nameof(length));
        }
    }

    private static void MulAddMultiCore(SimdLevel level, byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        nint done = level switch
        {
            SimdLevel.Avx2 => Avx2Multi(dst, srcs, tables, stride, count, length, overwrite),
            SimdLevel.Avx512 => Avx512Multi(dst, srcs, tables, stride, count, length, overwrite),
            SimdLevel.Ssse3 => Ssse3Multi(dst, srcs, tables, stride, count, length, overwrite),
            SimdLevel.Neon => NeonMulti(dst, srcs, tables, stride, count, length, overwrite),
            SimdLevel.Gfni128 => Gfni128Multi(dst, srcs, tables, stride, count, length, overwrite),
            SimdLevel.Gfni256 => Gfni256Multi(dst, srcs, tables, stride, count, length, overwrite),
            SimdLevel.Gfni512 => Gfni512Multi(dst, srcs, tables, stride, count, length, overwrite),
            _ => 0,
        };

        if (done < length)
        {
            ScalarTail(level, dst, srcs, tables, stride, count, done, length, overwrite);
        }
    }

    // Tail (and the full scalar path): uses the field via product lookups derived from the SIMD tables.
    private static void ScalarTail(SimdLevel level, byte* dst, byte** srcs, byte* tables, int stride, int count, nint start, nint length, bool overwrite)
    {
        for (nint pos = start; pos < length; pos += 2)
        {
            int acc = overwrite ? 0 : *(ushort*)(dst + pos);
            for (int f = 0; f < count; f++)
            {
                int x = *(ushort*)(srcs[f] + pos);
                if (x == 0)
                {
                    continue;
                }

                acc ^= ScalarProduct(level, tables + (nint)f * stride, x);
            }

            *(ushort*)(dst + pos) = (ushort)acc;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarProduct(SimdLevel level, byte* t, int x)
    {
        switch (level)
        {
            case SimdLevel.Scalar:
            {
                var tt = (ushort*)t;
                return tt[x & 0xFF] ^ tt[256 + (x >> 8)];
            }

            case SimdLevel.Ssse3:
            case SimdLevel.Neon:
            case SimdLevel.Avx2:
            case SimdLevel.Avx512:
            {
                int width = level switch { SimdLevel.Avx2 => 32, SimdLevel.Avx512 => 64, _ => 16 };
                int r = 0;
                for (int n = 0; n < 4; n++)
                {
                    int v = (x >> (4 * n)) & 15;
                    r ^= t[(n * 2) * width + v] | (t[(n * 2 + 1) * width + v] << 8);
                }

                return r;
            }

            default:
            {
                int width = level switch { SimdLevel.Gfni256 => 32, SimdLevel.Gfni512 => 64, _ => 16 };
                byte lo = (byte)x, hi = (byte)(x >> 8);
                ulong ll = *(ulong*)t, hl = *(ulong*)(t + width), lh = *(ulong*)(t + 2 * width), hh = *(ulong*)(t + 3 * width);
                int pl = AffineEmulate(lo, ll) ^ AffineEmulate(hi, hl);
                int ph = AffineEmulate(lo, lh) ^ AffineEmulate(hi, hh);
                return pl | (ph << 8);
            }
        }
    }

    // ---------------------------------------------------------------- AVX2 (split nibble tables, vpshufb)
    private static nint Avx2Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var mff = Vector256.Create((ushort)0x00FF);
        var m0f = Vector256.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 64 <= length; pos += 64)
        {
            var accL = Vector256<byte>.Zero;
            var accH = Vector256<byte>.Zero;
            for (int f = 0; f < count; f++)
            {
                byte* s = srcs[f] + pos;
                byte* t = tables + (nint)f * stride;
                var a = Avx.LoadVector256(s).AsUInt16();
                var b = Avx.LoadVector256(s + 32).AsUInt16();
                var lo = Avx2.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16());
                var hi = Avx2.PackUnsignedSaturate(Avx2.ShiftRightLogical(a, 8).AsInt16(), Avx2.ShiftRightLogical(b, 8).AsInt16());
                var n0 = lo & m0f;
                var n1 = Avx2.ShiftRightLogical(lo.AsUInt16(), 4).AsByte() & m0f;
                var n2 = hi & m0f;
                var n3 = Avx2.ShiftRightLogical(hi.AsUInt16(), 4).AsByte() & m0f;
                accL ^= Avx2.Shuffle(Avx.LoadVector256(t), n0) ^ Avx2.Shuffle(Avx.LoadVector256(t + 64), n1)
                      ^ Avx2.Shuffle(Avx.LoadVector256(t + 128), n2) ^ Avx2.Shuffle(Avx.LoadVector256(t + 192), n3);
                accH ^= Avx2.Shuffle(Avx.LoadVector256(t + 32), n0) ^ Avx2.Shuffle(Avx.LoadVector256(t + 96), n1)
                      ^ Avx2.Shuffle(Avx.LoadVector256(t + 160), n2) ^ Avx2.Shuffle(Avx.LoadVector256(t + 224), n3);
            }

            var outA = Avx2.UnpackLow(accL, accH);
            var outB = Avx2.UnpackHigh(accL, accH);
            if (!overwrite)
            {
                outA ^= Avx.LoadVector256(dst + pos);
                outB ^= Avx.LoadVector256(dst + pos + 32);
            }

            Avx.Store(dst + pos, outA);
            Avx.Store(dst + pos + 32, outB);
        }

        return pos;
    }

    // ---------------------------------------------------------------- AVX-512BW
    private static nint Avx512Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var mff = Vector512.Create((ushort)0x00FF);
        var m0f = Vector512.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 128 <= length; pos += 128)
        {
            var accL = Vector512<byte>.Zero;
            var accH = Vector512<byte>.Zero;
            for (int f = 0; f < count; f++)
            {
                byte* s = srcs[f] + pos;
                byte* t = tables + (nint)f * stride;
                var a = Vector512.Load(s).AsUInt16();
                var b = Vector512.Load(s + 64).AsUInt16();
                var lo = Avx512BW.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16());
                var hi = Avx512BW.PackUnsignedSaturate(Avx512BW.ShiftRightLogical(a, 8).AsInt16(), Avx512BW.ShiftRightLogical(b, 8).AsInt16());
                var n0 = lo & m0f;
                var n1 = Avx512BW.ShiftRightLogical(lo.AsUInt16(), 4).AsByte() & m0f;
                var n2 = hi & m0f;
                var n3 = Avx512BW.ShiftRightLogical(hi.AsUInt16(), 4).AsByte() & m0f;
                accL ^= Avx512BW.Shuffle(Vector512.Load(t), n0) ^ Avx512BW.Shuffle(Vector512.Load(t + 128), n1)
                      ^ Avx512BW.Shuffle(Vector512.Load(t + 256), n2) ^ Avx512BW.Shuffle(Vector512.Load(t + 384), n3);
                accH ^= Avx512BW.Shuffle(Vector512.Load(t + 64), n0) ^ Avx512BW.Shuffle(Vector512.Load(t + 192), n1)
                      ^ Avx512BW.Shuffle(Vector512.Load(t + 320), n2) ^ Avx512BW.Shuffle(Vector512.Load(t + 448), n3);
            }

            var outA = Avx512BW.UnpackLow(accL, accH);
            var outB = Avx512BW.UnpackHigh(accL, accH);
            if (!overwrite)
            {
                outA ^= Vector512.Load(dst + pos);
                outB ^= Vector512.Load(dst + pos + 64);
            }

            outA.Store(dst + pos);
            outB.Store(dst + pos + 64);
        }

        return pos;
    }

    // ---------------------------------------------------------------- SSSE3
    private static nint Ssse3Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var mff = Vector128.Create((ushort)0x00FF);
        var m0f = Vector128.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 32 <= length; pos += 32)
        {
            var accL = Vector128<byte>.Zero;
            var accH = Vector128<byte>.Zero;
            for (int f = 0; f < count; f++)
            {
                byte* s = srcs[f] + pos;
                byte* t = tables + (nint)f * stride;
                var a = Sse2.LoadVector128(s).AsUInt16();
                var b = Sse2.LoadVector128(s + 16).AsUInt16();
                var lo = Sse2.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16());
                var hi = Sse2.PackUnsignedSaturate(Sse2.ShiftRightLogical(a, 8).AsInt16(), Sse2.ShiftRightLogical(b, 8).AsInt16());
                var n0 = lo & m0f;
                var n1 = Sse2.ShiftRightLogical(lo.AsUInt16(), 4).AsByte() & m0f;
                var n2 = hi & m0f;
                var n3 = Sse2.ShiftRightLogical(hi.AsUInt16(), 4).AsByte() & m0f;
                accL ^= Ssse3.Shuffle(Sse2.LoadVector128(t), n0) ^ Ssse3.Shuffle(Sse2.LoadVector128(t + 32), n1)
                      ^ Ssse3.Shuffle(Sse2.LoadVector128(t + 64), n2) ^ Ssse3.Shuffle(Sse2.LoadVector128(t + 96), n3);
                accH ^= Ssse3.Shuffle(Sse2.LoadVector128(t + 16), n0) ^ Ssse3.Shuffle(Sse2.LoadVector128(t + 48), n1)
                      ^ Ssse3.Shuffle(Sse2.LoadVector128(t + 80), n2) ^ Ssse3.Shuffle(Sse2.LoadVector128(t + 112), n3);
            }

            var outA = Sse2.UnpackLow(accL, accH);
            var outB = Sse2.UnpackHigh(accL, accH);
            if (!overwrite)
            {
                outA ^= Sse2.LoadVector128(dst + pos);
                outB ^= Sse2.LoadVector128(dst + pos + 16);
            }

            Sse2.Store(dst + pos, outA);
            Sse2.Store(dst + pos + 16, outB);
        }

        return pos;
    }

    // ---------------------------------------------------------------- ARM64 NEON (tbl + uzp/zip)
    private static nint NeonMulti(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var m0f = Vector128.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 32 <= length; pos += 32)
        {
            var accL = Vector128<byte>.Zero;
            var accH = Vector128<byte>.Zero;
            for (int f = 0; f < count; f++)
            {
                byte* s = srcs[f] + pos;
                byte* t = tables + (nint)f * stride;
                var a = AdvSimd.LoadVector128(s);
                var b = AdvSimd.LoadVector128(s + 16);
                var lo = AdvSimd.Arm64.UnzipEven(a, b);
                var hi = AdvSimd.Arm64.UnzipOdd(a, b);
                var n0 = lo & m0f;
                var n1 = AdvSimd.ShiftRightLogical(lo, 4);
                var n2 = hi & m0f;
                var n3 = AdvSimd.ShiftRightLogical(hi, 4);
                accL ^= AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t), n0) ^ AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 32), n1)
                      ^ AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 64), n2) ^ AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 96), n3);
                accH ^= AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 16), n0) ^ AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 48), n1)
                      ^ AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 80), n2) ^ AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 112), n3);
            }

            var outA = AdvSimd.Arm64.ZipLow(accL, accH);
            var outB = AdvSimd.Arm64.ZipHigh(accL, accH);
            if (!overwrite)
            {
                outA ^= AdvSimd.LoadVector128(dst + pos);
                outB ^= AdvSimd.LoadVector128(dst + pos + 16);
            }

            AdvSimd.Store(dst + pos, outA);
            AdvSimd.Store(dst + pos + 16, outB);
        }

        return pos;
    }

    // ---------------------------------------------------------------- GFNI (4 affine transforms per symbol pair)
    private static nint Gfni128Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var mff = Vector128.Create((ushort)0x00FF);
        nint pos = 0;
        for (; pos + 32 <= length; pos += 32)
        {
            var accL = Vector128<byte>.Zero;
            var accH = Vector128<byte>.Zero;
            for (int f = 0; f < count; f++)
            {
                byte* s = srcs[f] + pos;
                byte* t = tables + (nint)f * stride;
                var a = Sse2.LoadVector128(s).AsUInt16();
                var b = Sse2.LoadVector128(s + 16).AsUInt16();
                var lo = Sse2.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16());
                var hi = Sse2.PackUnsignedSaturate(Sse2.ShiftRightLogical(a, 8).AsInt16(), Sse2.ShiftRightLogical(b, 8).AsInt16());
                accL ^= Gfni.GaloisFieldAffineTransform(lo, Sse2.LoadVector128(t), 0) ^ Gfni.GaloisFieldAffineTransform(hi, Sse2.LoadVector128(t + 16), 0);
                accH ^= Gfni.GaloisFieldAffineTransform(lo, Sse2.LoadVector128(t + 32), 0) ^ Gfni.GaloisFieldAffineTransform(hi, Sse2.LoadVector128(t + 48), 0);
            }

            var outA = Sse2.UnpackLow(accL, accH);
            var outB = Sse2.UnpackHigh(accL, accH);
            if (!overwrite)
            {
                outA ^= Sse2.LoadVector128(dst + pos);
                outB ^= Sse2.LoadVector128(dst + pos + 16);
            }

            Sse2.Store(dst + pos, outA);
            Sse2.Store(dst + pos + 16, outB);
        }

        return pos;
    }

    private static nint Gfni256Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var mff = Vector256.Create((ushort)0x00FF);
        nint pos = 0;
        for (; pos + 64 <= length; pos += 64)
        {
            var accL = Vector256<byte>.Zero;
            var accH = Vector256<byte>.Zero;
            for (int f = 0; f < count; f++)
            {
                byte* s = srcs[f] + pos;
                byte* t = tables + (nint)f * stride;
                var a = Avx.LoadVector256(s).AsUInt16();
                var b = Avx.LoadVector256(s + 32).AsUInt16();
                var lo = Avx2.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16());
                var hi = Avx2.PackUnsignedSaturate(Avx2.ShiftRightLogical(a, 8).AsInt16(), Avx2.ShiftRightLogical(b, 8).AsInt16());
                accL ^= Gfni.V256.GaloisFieldAffineTransform(lo, Avx.LoadVector256(t), 0) ^ Gfni.V256.GaloisFieldAffineTransform(hi, Avx.LoadVector256(t + 32), 0);
                accH ^= Gfni.V256.GaloisFieldAffineTransform(lo, Avx.LoadVector256(t + 64), 0) ^ Gfni.V256.GaloisFieldAffineTransform(hi, Avx.LoadVector256(t + 96), 0);
            }

            var outA = Avx2.UnpackLow(accL, accH);
            var outB = Avx2.UnpackHigh(accL, accH);
            if (!overwrite)
            {
                outA ^= Avx.LoadVector256(dst + pos);
                outB ^= Avx.LoadVector256(dst + pos + 32);
            }

            Avx.Store(dst + pos, outA);
            Avx.Store(dst + pos + 32, outB);
        }

        return pos;
    }

    private static nint Gfni512Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var mff = Vector512.Create((ushort)0x00FF);
        nint pos = 0;
        for (; pos + 128 <= length; pos += 128)
        {
            var accL = Vector512<byte>.Zero;
            var accH = Vector512<byte>.Zero;
            for (int f = 0; f < count; f++)
            {
                byte* s = srcs[f] + pos;
                byte* t = tables + (nint)f * stride;
                var a = Vector512.Load(s).AsUInt16();
                var b = Vector512.Load(s + 64).AsUInt16();
                var lo = Avx512BW.PackUnsignedSaturate((a & mff).AsInt16(), (b & mff).AsInt16());
                var hi = Avx512BW.PackUnsignedSaturate(Avx512BW.ShiftRightLogical(a, 8).AsInt16(), Avx512BW.ShiftRightLogical(b, 8).AsInt16());
                accL ^= Gfni.V512.GaloisFieldAffineTransform(lo, Vector512.Load(t), 0) ^ Gfni.V512.GaloisFieldAffineTransform(hi, Vector512.Load(t + 64), 0);
                accH ^= Gfni.V512.GaloisFieldAffineTransform(lo, Vector512.Load(t + 128), 0) ^ Gfni.V512.GaloisFieldAffineTransform(hi, Vector512.Load(t + 192), 0);
            }

            var outA = Avx512BW.UnpackLow(accL, accH);
            var outB = Avx512BW.UnpackHigh(accL, accH);
            if (!overwrite)
            {
                outA ^= Vector512.Load(dst + pos);
                outB ^= Vector512.Load(dst + pos + 64);
            }

            outA.Store(dst + pos);
            outB.Store(dst + pos + 64);
        }

        return pos;
    }
}

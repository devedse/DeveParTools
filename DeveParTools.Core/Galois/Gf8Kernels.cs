using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DeveParTools.Core.Galois;

/// <summary>Pre-computed multiplication tables for a list of GF(2^8) coefficients.</summary>
public sealed unsafe class Gf8MulTables : IDisposable
{
    internal byte* Ptr;
    public int Count { get; }
    public int Stride { get; }
    public SimdLevel Level { get; }
    public IGf8Multiplier Field { get; }

    /// <summary>The tables of the 32 coefficients u·16^m (see <see cref="Gf8Kernels.Basis"/>); kept alive for <see cref="_basisPtr"/>.</summary>
    private readonly byte[] _basis;
    private readonly byte* _basisPtr;

    private Gf8MulTables(IGf8Multiplier field, int count, SimdLevel level)
    {
        Field = field;
        Count = count;
        Level = level;
        Stride = Gf8Kernels.TableStride(level);
        Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, count * Stride), 64);
        _basis = Gf8Kernels.Basis(field, level);
        _basisPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_basis));
    }

    public static Gf8MulTables Create(IGf8Multiplier field, ReadOnlySpan<byte> coefficients, SimdLevel? level = null)
    {
        var t = new Gf8MulTables(field, coefficients.Length, level ?? Gf8Kernels.Level);
        t.Set(0, coefficients);
        return t;
    }

    /// <summary>Replace the coefficients from <paramref name="first"/> on with <paramref name="coefficients"/> (cheap: see <see cref="Gf8Kernels.TableFromBasis"/>).</summary>
    public void Set(int first, ReadOnlySpan<byte> coefficients)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(coefficients.Length, Count - first, nameof(coefficients));
        for (int k = 0; k < coefficients.Length; k++)
        {
            Gf8Kernels.TableFromBasis(_basisPtr, Stride, coefficients[k], Ptr + (nint)(first + k) * Stride);
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

    ~Gf8MulTables() => Dispose();
}

/// <summary>SIMD kernels for GF(2^8) region arithmetic: <c>dst ^= c0*src0 ^ c1*src1 ^ ...</c>.</summary>
public static unsafe class Gf8Kernels
{
    private static SimdLevel _level = SimdSupport.Best;

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
        SimdLevel.Scalar => 256,
        SimdLevel.Ssse3 or SimdLevel.Neon => 32,
        SimdLevel.Avx2 => 64,
        SimdLevel.Avx512 => 128,
        SimdLevel.Gfni128 => 16,
        SimdLevel.Gfni256 => 32,
        SimdLevel.Gfni512 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>GFNI affine matrix for multiplying a byte by <paramref name="c"/> in <paramref name="field"/>.</summary>
    public static ulong GetAffineMatrix(IGf8Multiplier field, byte c)
    {
        ulong m = 0;
        for (int i = 0; i < 8; i++)
        {
            ulong row = 0;
            for (int j = 0; j < 8; j++)
            {
                row |= (ulong)((field.Mul(c, (byte)(1 << j)) >> i) & 1) << j;
            }

            m |= row << (8 * (7 - i));
        }

        return m;
    }

    private static readonly ConditionalWeakTable<IGf8Multiplier, byte[]?[]> Bases = new();

    /// <summary>
    /// The tables (in <paramref name="level"/>'s layout) of the 32 coefficients u·16^m, u = 0..15, m = 0..1, row
    /// 16·m + u, in pinned memory; built once per field and level. See <see cref="Gf16Kernels.Basis"/>.
    /// </summary>
    internal static byte[] Basis(IGf8Multiplier field, SimdLevel level)
    {
        var perLevel = Bases.GetValue(field, static _ => new byte[]?[Enum.GetValues<SimdLevel>().Length]);
        return perLevel[(int)level] ??= BuildBasis(field, level);
    }

    /// <summary>The table of <paramref name="c"/>: the XOR of the tables of its two nibbles (multiplying is linear in c).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void TableFromBasis(byte* basis, int stride, int c, byte* dst)
    {
        byte* r0 = basis + (c & 15) * stride;
        byte* r1 = basis + (16 + ((c >> 4) & 15)) * stride;
        for (int i = 0; i < stride; i += 16)
        {
            (Vector128.Load(r0 + i) ^ Vector128.Load(r1 + i)).Store(dst + i);
        }
    }

    private static byte[] BuildBasis(IGf8Multiplier field, SimdLevel level)
    {
        int stride = TableStride(level);
        var basis = GC.AllocateArray<byte>(32 * stride, pinned: true);
        fixed (byte* p = basis)
        {
            for (int row = 0; row < 32; row++)
            {
                BuildTable(field, (byte)((row & 15) << (4 * (row >> 4))), level, p + row * stride);
            }
        }

        return basis;
    }

    internal static void BuildTable(IGf8Multiplier field, byte c, SimdLevel level, byte* dst)
    {
        switch (level)
        {
            case SimdLevel.Scalar:
                for (int b = 0; b < 256; b++)
                {
                    dst[b] = field.Mul(c, (byte)b);
                }

                break;
            case SimdLevel.Ssse3:
            case SimdLevel.Neon:
            case SimdLevel.Avx2:
            case SimdLevel.Avx512:
            {
                int width = level switch { SimdLevel.Avx2 => 32, SimdLevel.Avx512 => 64, _ => 16 };
                for (int v = 0; v < 16; v++)
                {
                    byte pl = field.Mul(c, (byte)v);
                    byte ph = field.Mul(c, (byte)(v << 4));
                    for (int rep = 0; rep < width; rep += 16)
                    {
                        dst[rep + v] = pl;
                        dst[width + rep + v] = ph;
                    }
                }

                break;
            }

            default:
            {
                int width = level switch { SimdLevel.Gfni256 => 32, SimdLevel.Gfni512 => 64, _ => 16 };
                ulong m = GetAffineMatrix(field, c);
                for (int rep = 0; rep < width; rep += 8)
                {
                    *(ulong*)(dst + rep) = m;
                }

                break;
            }
        }
    }

    /// <summary><c>dst ^= c * src</c> (or <c>dst = c * src</c> when <paramref name="overwrite"/>).</summary>
    public static void MulAdd(IGf8Multiplier field, byte c, ReadOnlySpan<byte> src, Span<byte> dst, bool overwrite = false)
    {
        if (src.Length != dst.Length)
        {
            throw new ArgumentException("Source and destination must have the same length.");
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
        byte* table = stackalloc byte[256];
        fixed (byte* basis = Basis(field, level))
        {
            TableFromBasis(basis, TableStride(level), c, table);
        }

        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            byte* sp = s;
            MulAddMultiCore(level, field, d, &sp, table, TableStride(level), 1, dst.Length, overwrite);
        }
    }

    public static void MulAddMulti(Gf8MulTables tables, int first, int count, byte** srcs, byte* dst, nint length, bool overwrite = false)
    {
        if (count <= 0)
        {
            if (overwrite)
            {
                new Span<byte>(dst, checked((int)length)).Clear();
            }

            return;
        }

        MulAddMultiCore(tables.Level, tables.Field, dst, srcs, tables.Ptr + (nint)first * tables.Stride, tables.Stride, count, length, overwrite);
    }

    /// <summary>
    /// <c>dst (^)= Σ coefficients[i]·srcs[i]</c>, the tables built on the way from the basis (cheap: see
    /// <see cref="TableFromBasis"/>), 16 sources per kernel call.
    /// </summary>
    internal static void MulAddCoefficients(IGf8Multiplier field, SimdLevel level, byte* basis, ReadOnlySpan<byte> coefficients, byte** srcs, byte* dst, nint length, bool overwrite)
    {
        if (coefficients.Length == 0)
        {
            if (overwrite)
            {
                new Span<byte>(dst, checked((int)length)).Clear();
            }

            return;
        }

        int stride = TableStride(level);
        byte* tables = stackalloc byte[16 * 256];
        for (int g = 0; g < coefficients.Length; g += 16)
        {
            int n = Math.Min(16, coefficients.Length - g);
            for (int k = 0; k < n; k++)
            {
                TableFromBasis(basis, stride, coefficients[g + k], tables + k * stride);
            }

            MulAddMultiCore(level, field, dst, srcs + g, tables, stride, n, length, overwrite && g == 0);
        }
    }

    private static void MulAddMultiCore(SimdLevel level, IGf8Multiplier field, byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
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
            _ => ScalarMulti(dst, srcs, tables, stride, count, length, overwrite),
        };

        if (done < length)
        {
            for (nint pos = done; pos < length; pos++)
            {
                int acc = overwrite ? 0 : dst[pos];
                for (int f = 0; f < count; f++)
                {
                    acc ^= ScalarProduct(level, field, tables + (nint)f * stride, srcs[f][pos]);
                }

                dst[pos] = (byte)acc;
            }
        }
    }

    /// <summary>
    /// Portable kernel: one 256-entry product table per coefficient, applied source by source, eight bytes per step.
    /// The eight looked-up products are packed back into a 64-bit word, so the destination is read and written once per
    /// eight bytes. The byte order does not matter: byte k of the result is the product of byte k of the input.
    /// </summary>
    private static nint ScalarMulti(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        nint words = length & ~(nint)7;
        for (int f = 0; f < count; f++)
        {
            byte* t = tables + (nint)f * stride;
            byte* s = srcs[f];
            bool store = overwrite && f == 0;
            for (nint pos = 0; pos < words; pos += 8)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(s + pos);
                ulong p = t[(byte)x]
                    | ((ulong)t[(byte)(x >> 8)] << 8)
                    | ((ulong)t[(byte)(x >> 16)] << 16)
                    | ((ulong)t[(byte)(x >> 24)] << 24)
                    | ((ulong)t[(byte)(x >> 32)] << 32)
                    | ((ulong)t[(byte)(x >> 40)] << 40)
                    | ((ulong)t[(byte)(x >> 48)] << 48)
                    | ((ulong)t[(byte)(x >> 56)] << 56);
                Unsafe.WriteUnaligned(dst + pos, store ? p : p ^ Unsafe.ReadUnaligned<ulong>(dst + pos));
            }
        }

        return words;
    }

    private static int ScalarProduct(SimdLevel level, IGf8Multiplier field, byte* t, byte x)
    {
        switch (level)
        {
            case SimdLevel.Scalar:
                return t[x];
            case SimdLevel.Ssse3:
            case SimdLevel.Neon:
            case SimdLevel.Avx2:
            case SimdLevel.Avx512:
            {
                int width = level switch { SimdLevel.Avx2 => 32, SimdLevel.Avx512 => 64, _ => 16 };
                return t[x & 15] ^ t[width + (x >> 4)];
            }

            default:
                return Gf16Kernels.AffineEmulate(x, *(ulong*)t);
        }
    }

    private static nint Avx2Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var m0f = Vector256.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 32 <= length; pos += 32)
        {
            var acc = overwrite ? Vector256<byte>.Zero : Avx.LoadVector256(dst + pos);
            for (int f = 0; f < count; f++)
            {
                var x = Avx.LoadVector256(srcs[f] + pos);
                byte* t = tables + (nint)f * stride;
                acc ^= Avx2.Shuffle(Avx.LoadVector256(t), x & m0f) ^ Avx2.Shuffle(Avx.LoadVector256(t + 32), Avx2.ShiftRightLogical(x.AsUInt16(), 4).AsByte() & m0f);
            }

            Avx.Store(dst + pos, acc);
        }

        return pos;
    }

    private static nint Avx512Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var m0f = Vector512.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 64 <= length; pos += 64)
        {
            var acc = overwrite ? Vector512<byte>.Zero : Vector512.Load(dst + pos);
            for (int f = 0; f < count; f++)
            {
                var x = Vector512.Load(srcs[f] + pos);
                byte* t = tables + (nint)f * stride;
                acc ^= Avx512BW.Shuffle(Vector512.Load(t), x & m0f) ^ Avx512BW.Shuffle(Vector512.Load(t + 64), Avx512BW.ShiftRightLogical(x.AsUInt16(), 4).AsByte() & m0f);
            }

            acc.Store(dst + pos);
        }

        return pos;
    }

    private static nint Ssse3Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var m0f = Vector128.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 16 <= length; pos += 16)
        {
            var acc = overwrite ? Vector128<byte>.Zero : Sse2.LoadVector128(dst + pos);
            for (int f = 0; f < count; f++)
            {
                var x = Sse2.LoadVector128(srcs[f] + pos);
                byte* t = tables + (nint)f * stride;
                acc ^= Ssse3.Shuffle(Sse2.LoadVector128(t), x & m0f) ^ Ssse3.Shuffle(Sse2.LoadVector128(t + 16), Sse2.ShiftRightLogical(x.AsUInt16(), 4).AsByte() & m0f);
            }

            Sse2.Store(dst + pos, acc);
        }

        return pos;
    }

    private static nint NeonMulti(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        var m0f = Vector128.Create((byte)0x0F);
        nint pos = 0;
        for (; pos + 16 <= length; pos += 16)
        {
            var acc = overwrite ? Vector128<byte>.Zero : AdvSimd.LoadVector128(dst + pos);
            for (int f = 0; f < count; f++)
            {
                var x = AdvSimd.LoadVector128(srcs[f] + pos);
                byte* t = tables + (nint)f * stride;
                acc ^= AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t), x & m0f) ^ AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(t + 16), AdvSimd.ShiftRightLogical(x, 4));
            }

            AdvSimd.Store(dst + pos, acc);
        }

        return pos;
    }

    private static nint Gfni128Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        nint pos = 0;
        for (; pos + 16 <= length; pos += 16)
        {
            var acc = overwrite ? Vector128<byte>.Zero : Sse2.LoadVector128(dst + pos);
            for (int f = 0; f < count; f++)
            {
                acc ^= Gfni.GaloisFieldAffineTransform(Sse2.LoadVector128(srcs[f] + pos), Sse2.LoadVector128(tables + (nint)f * stride), 0);
            }

            Sse2.Store(dst + pos, acc);
        }

        return pos;
    }

    private static nint Gfni256Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        nint pos = 0;
        for (; pos + 32 <= length; pos += 32)
        {
            var acc = overwrite ? Vector256<byte>.Zero : Avx.LoadVector256(dst + pos);
            for (int f = 0; f < count; f++)
            {
                acc ^= Gfni.V256.GaloisFieldAffineTransform(Avx.LoadVector256(srcs[f] + pos), Avx.LoadVector256(tables + (nint)f * stride), 0);
            }

            Avx.Store(dst + pos, acc);
        }

        return pos;
    }

    private static nint Gfni512Multi(byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
    {
        nint pos = 0;
        for (; pos + 64 <= length; pos += 64)
        {
            var acc = overwrite ? Vector512<byte>.Zero : Vector512.Load(dst + pos);
            for (int f = 0; f < count; f++)
            {
                acc ^= Gfni.V512.GaloisFieldAffineTransform(Vector512.Load(srcs[f] + pos), Vector512.Load(tables + (nint)f * stride), 0);
            }

            acc.Store(dst + pos);
        }

        return pos;
    }
}

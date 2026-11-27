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
    public GaloisField8 Field { get; }

    private Gf8MulTables(GaloisField8 field, int count, SimdLevel level)
    {
        Field = field;
        Count = count;
        Level = level;
        Stride = Gf8Kernels.TableStride(level);
        Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, count * Stride), 64);
    }

    public static Gf8MulTables Create(GaloisField8 field, ReadOnlySpan<byte> coefficients, SimdLevel? level = null)
    {
        var lvl = level ?? Gf8Kernels.Level;
        var t = new Gf8MulTables(field, coefficients.Length, lvl);
        for (int i = 0; i < coefficients.Length; i++)
        {
            Gf8Kernels.BuildTable(field, coefficients[i], lvl, t.Ptr + (nint)i * t.Stride);
        }

        return t;
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
    public static ulong GetAffineMatrix(GaloisField8 field, byte c)
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

    internal static void BuildTable(GaloisField8 field, byte c, SimdLevel level, byte* dst)
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
    public static void MulAdd(GaloisField8 field, byte c, ReadOnlySpan<byte> src, Span<byte> dst, bool overwrite = false)
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
        BuildTable(field, c, level, table);
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

    private static void MulAddMultiCore(SimdLevel level, GaloisField8 field, byte* dst, byte** srcs, byte* tables, int stride, int count, nint length, bool overwrite)
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

    private static int ScalarProduct(SimdLevel level, GaloisField8 field, byte* t, byte x)
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

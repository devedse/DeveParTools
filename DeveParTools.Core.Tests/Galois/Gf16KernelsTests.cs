using System.Runtime.CompilerServices;
using DeveParTools.Core.Codes.Fft;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Galois;

/// <summary>
/// <see cref="Gf16Kernels"/> beyond the multi-source reference test in <see cref="GaloisKernelTests"/>: the single
/// <c>MulAdd</c> at every SIMD level, argument checks, zero symbols and the table objects.
/// </summary>
[TestClass]
public sealed unsafe class Gf16KernelsTests
{
    private readonly SimdLevel _savedLevel = Gf16Kernels.Level;

    public static IEnumerable<object[]> Levels => SimdSupport.SupportedLevels.Select(l => new object[] { l });

    [TestCleanup]
    public void RestoreLevel() => Gf16Kernels.Level = _savedLevel;

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void MulAdd_AtEveryLevel_MatchesScalarMultiplication(SimdLevel level)
    {
        Gf16Kernels.Level = level;
        Assert.AreEqual(level, Gf16Kernels.Level);
        var rnd = new Random(200 + (int)level);
        foreach (IGf16Multiplier field in new IGf16Multiplier[] { GaloisField16.Par2, LeopardField.FF16 })
        {
            // Lengths around every block width (32, 64, 128 bytes) so both the SIMD body and the scalar tail run.
            foreach (int length in new[] { 0, 2, 30, 32, 62, 66, 128, 254, 400 })
            {
                ushort c = (ushort)rnd.Next(1, 65536);
                var src = TestData.RandomBytes(rnd, length);
                var dst = TestData.RandomBytes(rnd, length);
                var expectedSet = Map(src, x => field.Mul(c, x));
                var expectedAdd = Xor(dst, expectedSet);

                var add = (byte[])dst.Clone();
                Gf16Kernels.MulAdd(field, c, src, add);
                CollectionAssert.AreEqual(expectedAdd, add, $"{field} level={level} length={length} add");

                var set = (byte[])dst.Clone();
                Gf16Kernels.MulAdd(field, c, src, set, overwrite: true);
                CollectionAssert.AreEqual(expectedSet, set, $"{field} level={level} length={length} overwrite");

                var inPlace = (byte[])src.Clone();
                Gf16Kernels.MulAdd(field, c, inPlace, inPlace, overwrite: true);
                CollectionAssert.AreEqual(expectedSet, inPlace, $"{field} level={level} length={length} in place");
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void MulAddMulti_ZeroSymbolsInTheTail_AreSkippedCorrectly(SimdLevel level)
    {
        // The scalar tail skips zero source symbols; mix zero and non-zero symbols in a tail-only length.
        var f = GaloisField16.Par2;
        var a = new byte[] { 0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0, 0 };
        var b = new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0x80 };
        using var tables = Gf16MulTables.Create(f, [0x1234, 0x8001], level);
        var dst = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var expected = Xor(dst, Xor(Map(a, x => f.Mul(0x1234, x)), Map(b, x => f.Mul(0x8001, x))));
        using (var pins = new PinnedArrays([a, b]))
        {
            fixed (byte* d = dst)
            {
                Gf16Kernels.MulAddMulti(tables, 0, 2, pins.Pointers, d, dst.Length);
            }
        }

        CollectionAssert.AreEqual(expected, dst);
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void Tables_SetReplacesOneCoefficient(SimdLevel level)
    {
        var f = GaloisField16.Par2;
        var rnd = new Random(9);
        var a = TestData.RandomBytes(rnd, 300);
        var b = TestData.RandomBytes(rnd, 300);
        using var tables = Gf16MulTables.Create(f, [5, 7, 11], level);
        Assert.AreEqual(3, tables.Count);
        Assert.AreEqual(level, tables.Level);
        Assert.AreEqual(Gf16Kernels.TableStride(level), tables.Stride);
        Assert.AreSame(f, tables.Field);
        tables.Set(2, 0xBEEF);
        var dst = new byte[300];
        using (var pins = new PinnedArrays([a, b]))
        {
            fixed (byte* d = dst)
            {
                // first = 1: coefficients 7 and (replaced) 0xBEEF.
                Gf16Kernels.MulAddMulti(tables, 1, 2, pins.Pointers, d, dst.Length, overwrite: true);
            }
        }

        CollectionAssert.AreEqual(Xor(Map(a, x => f.Mul(7, x)), Map(b, x => f.Mul(0xBEEF, x))), dst);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tables.Set(-1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tables.Set(3, 1));
    }

    [TestMethod]
    public void MulAdd_ZeroCoefficient_AddsNothingOrClears()
    {
        var src = new byte[] { 1, 2, 3, 4 };
        var dst = new byte[] { 5, 6, 7, 8 };
        Gf16Kernels.MulAdd(GaloisField16.Par2, 0, src, dst);
        CollectionAssert.AreEqual(new byte[] { 5, 6, 7, 8 }, dst);
        Gf16Kernels.MulAdd(GaloisField16.Par2, 0, src, dst, overwrite: true);
        CollectionAssert.AreEqual(new byte[4], dst);
    }

    [TestMethod]
    public void BadLengths_Throw()
    {
        var f = GaloisField16.Par2;
        Assert.ThrowsExactly<ArgumentException>(() => Gf16Kernels.MulAdd(f, 3, new byte[4], new byte[6]));
        Assert.ThrowsExactly<ArgumentException>(() => Gf16Kernels.MulAdd(f, 3, new byte[3], new byte[3]));
        using var tables = Gf16MulTables.Create(f, [1]);
        var dst = new byte[3];
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            fixed (byte* d = dst)
            {
                Gf16Kernels.MulAddMulti(tables, 0, 0, null, d, 3);
            }
        });
    }

    [TestMethod]
    public void MulAddMulti_NoSources_ClearsOnlyWhenOverwriting()
    {
        using var tables = Gf16MulTables.Create(GaloisField16.Par2, []);
        var dst = new byte[] { 9, 8, 7, 6 };
        fixed (byte* d = dst)
        {
            Gf16Kernels.MulAddMulti(tables, 0, 0, null, d, dst.Length);
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, dst);
            Gf16Kernels.MulAddMulti(tables, 0, 0, null, d, dst.Length, overwrite: true);
            CollectionAssert.AreEqual(new byte[4], dst);
        }
    }

    [TestMethod]
    public void Level_UnsupportedValue_ThrowsAndKeepsTheOldLevel()
    {
        var before = Gf16Kernels.Level;
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => Gf16Kernels.Level = (SimdLevel)99);
        foreach (var level in Enum.GetValues<SimdLevel>().Where(l => !SimdSupport.IsSupported(l)))
        {
            Assert.ThrowsExactly<PlatformNotSupportedException>(() => Gf16Kernels.Level = level);
        }

        Assert.AreEqual(before, Gf16Kernels.Level);
    }

    [TestMethod]
    public void UnknownLevel_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Gf16MulTables.Create(GaloisField16.Par2, [1], (SimdLevel)99));
        var table = new byte[1024];
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            fixed (byte* t = table)
            {
                Gf16Kernels.BuildTable(GaloisField16.Par2, 1, (SimdLevel)99, t);
            }
        });
    }

    [TestMethod]
    public void TableStrides_FitTheSingleMulAddStackTable()
    {
        foreach (var level in Enum.GetValues<SimdLevel>())
        {
            Assert.IsTrue(Gf16Kernels.TableStride(level) <= 1024, level.ToString());
        }
    }

    [TestMethod]
    public void Tables_DisposeTwiceAndFinalizerAreSafe()
    {
        var t = Gf16MulTables.Create(GaloisField16.Par2, [1, 2]);
        t.Dispose();
        t.Dispose();
        var weak = CreateAndDrop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(weak.IsAlive);
    }

    [TestMethod]
    public void AffineEmulate_IdentityMatrixReturnsTheInput()
    {
        // Row i of the matrix (byte 7 - i) selects the bits of output bit i: the identity has row i = 1 << i.
        const ulong identity = 0x0102040810204080UL;
        for (int x = 0; x < 256; x++)
        {
            Assert.AreEqual((byte)x, Gf16Kernels.AffineEmulate((byte)x, identity));
            Assert.AreEqual(0, Gf16Kernels.AffineEmulate((byte)x, 0));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDrop() => new(Gf16MulTables.Create(GaloisField16.Par2, [3, 4, 5]));

    private static byte[] Map(byte[] src, Func<ushort, ushort> f)
    {
        var r = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 2)
        {
            ushort v = f((ushort)(src[i] | (src[i + 1] << 8)));
            r[i] = (byte)v;
            r[i + 1] = (byte)(v >> 8);
        }

        return r;
    }

    private static byte[] Xor(byte[] a, byte[] b) => a.Select((x, i) => (byte)(x ^ b[i])).ToArray();
}

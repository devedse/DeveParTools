using System.Runtime.CompilerServices;
using DeveParTools.Core.Codes.Fft;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Galois;

/// <summary>
/// <see cref="Gf8Kernels"/> beyond the multi-source reference test in <see cref="GaloisKernelTests"/>: the single
/// <c>MulAdd</c> at every SIMD level, argument checks and the table objects.
/// </summary>
[TestClass]
public sealed unsafe class Gf8KernelsTests
{
    private readonly SimdLevel _savedLevel = Gf8Kernels.Level;

    public static IEnumerable<object[]> Levels => SimdSupport.SupportedLevels.Select(l => new object[] { l });

    [TestCleanup]
    public void RestoreLevel() => Gf8Kernels.Level = _savedLevel;

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void MulAdd_AtEveryLevel_MatchesScalarMultiplication(SimdLevel level)
    {
        Gf8Kernels.Level = level;
        Assert.AreEqual(level, Gf8Kernels.Level);
        var rnd = new Random(100 + (int)level);
        foreach (IGf8Multiplier field in new IGf8Multiplier[] { GaloisField8.Default, LeopardField.FF8 })
        {
            // Lengths around every vector width (16, 32, 64) so both the SIMD body and the scalar tail run.
            foreach (int length in new[] { 0, 1, 15, 16, 31, 33, 64, 127, 200 })
            {
                byte c = (byte)rnd.Next(1, 256);
                var src = TestData.RandomBytes(rnd, length);
                var dst = TestData.RandomBytes(rnd, length);
                var expectedAdd = dst.Select((d, i) => (byte)(d ^ field.Mul(c, src[i]))).ToArray();
                var expectedSet = src.Select(s => field.Mul(c, s)).ToArray();

                var add = (byte[])dst.Clone();
                Gf8Kernels.MulAdd(field, c, src, add);
                CollectionAssert.AreEqual(expectedAdd, add, $"{field} level={level} length={length} add");

                var set = (byte[])dst.Clone();
                Gf8Kernels.MulAdd(field, c, src, set, overwrite: true);
                CollectionAssert.AreEqual(expectedSet, set, $"{field} level={level} length={length} overwrite");

                // In place (source == destination), as RowScale uses it.
                var inPlace = (byte[])src.Clone();
                Gf8Kernels.MulAdd(field, c, inPlace, inPlace, overwrite: true);
                CollectionAssert.AreEqual(expectedSet, inPlace, $"{field} level={level} length={length} in place");
            }
        }
    }

    [TestMethod]
    public void MulAdd_ZeroCoefficient_AddsNothingOrClears()
    {
        var src = new byte[] { 1, 2, 3 };
        var dst = new byte[] { 4, 5, 6 };
        Gf8Kernels.MulAdd(GaloisField8.Default, 0, src, dst);
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, dst);
        Gf8Kernels.MulAdd(GaloisField8.Default, 0, src, dst, overwrite: true);
        CollectionAssert.AreEqual(new byte[3], dst);
    }

    [TestMethod]
    public void MulAdd_LengthMismatch_Throws()
        => Assert.ThrowsExactly<ArgumentException>(() => Gf8Kernels.MulAdd(GaloisField8.Default, 3, new byte[4], new byte[5]));

    [TestMethod]
    public void Level_UnsupportedValue_ThrowsAndKeepsTheOldLevel()
    {
        var before = Gf8Kernels.Level;
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => Gf8Kernels.Level = (SimdLevel)99);
        var unsupported = Enum.GetValues<SimdLevel>().Where(l => !SimdSupport.IsSupported(l)).ToArray();
        foreach (var level in unsupported)
        {
            Assert.ThrowsExactly<PlatformNotSupportedException>(() => Gf8Kernels.Level = level);
        }

        Assert.AreEqual(before, Gf8Kernels.Level);
    }

    [TestMethod]
    public void MulAddMulti_NoSources_ClearsOnlyWhenOverwriting()
    {
        using var tables = Gf8MulTables.Create(GaloisField8.Default, []);
        Assert.AreEqual(0, tables.Count);
        var dst = new byte[] { 9, 8, 7 };
        fixed (byte* d = dst)
        {
            Gf8Kernels.MulAddMulti(tables, 0, 0, null, d, dst.Length);
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, dst);
            Gf8Kernels.MulAddMulti(tables, 0, 0, null, d, dst.Length, overwrite: true);
            CollectionAssert.AreEqual(new byte[3], dst);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void MulAddMulti_FirstSelectsTheCoefficients(SimdLevel level)
    {
        // Tables for coefficients [5, 7, 11]; first = 1, count = 2 must use 7 and 11.
        var f = GaloisField8.Default;
        var rnd = new Random(7);
        var a = TestData.RandomBytes(rnd, 100);
        var b = TestData.RandomBytes(rnd, 100);
        using var tables = Gf8MulTables.Create(f, [5, 7, 11], level);
        Assert.AreEqual(level, tables.Level);
        Assert.AreEqual(Gf8Kernels.TableStride(level), tables.Stride);
        Assert.AreSame(f, tables.Field);
        var dst = new byte[100];
        using (var pins = new PinnedArrays([a, b]))
        {
            fixed (byte* d = dst)
            {
                Gf8Kernels.MulAddMulti(tables, 1, 2, pins.Pointers, d, dst.Length, overwrite: true);
            }
        }

        CollectionAssert.AreEqual(a.Select((x, i) => (byte)(f.Mul(7, x) ^ f.Mul(11, b[i]))).ToArray(), dst);
    }

    [TestMethod]
    public void Tables_UnknownLevel_Throws()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Gf8MulTables.Create(GaloisField8.Default, [1], (SimdLevel)99));

    [TestMethod]
    public void Tables_DisposeTwiceAndFinalizerAreSafe()
    {
        var t = Gf8MulTables.Create(GaloisField8.Default, [1, 2]);
        t.Dispose();
        t.Dispose();

        // An undisposed table is freed by its finalizer.
        var weak = CreateAndDrop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(weak.IsAlive);
    }

    [TestMethod]
    public void TableStrides_FitTheSingleMulAddStackTable()
    {
        foreach (var level in Enum.GetValues<SimdLevel>())
        {
            // MulAdd builds one table on the stack (256 bytes).
            Assert.IsTrue(Gf8Kernels.TableStride(level) <= 256, level.ToString());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDrop() => new(Gf8MulTables.Create(GaloisField8.Default, [3, 4, 5]));
}

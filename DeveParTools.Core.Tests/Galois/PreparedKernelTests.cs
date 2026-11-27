using DeveParTools.Core.Codes.Fft;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Galois;

/// <summary>
/// The prepared layout and the single-source kernels the combiner's tiled loop uses (<see cref="IPreparedKernel{T}"/>),
/// at every SIMD level, against the normal kernels; and the cheap table rebuild from the basis.
/// </summary>
[TestClass]
public sealed unsafe class PreparedKernelTests
{
    private readonly SimdLevel _saved16 = Gf16Kernels.Level;
    private readonly SimdLevel _saved8 = Gf8Kernels.Level;

    public static IEnumerable<object[]> Levels => SimdSupport.SupportedLevels.Select(l => new object[] { l });

    [TestCleanup]
    public void RestoreLevels()
    {
        Gf16Kernels.Level = _saved16;
        Gf8Kernels.Level = _saved8;
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void PrepareThenFinish_GivesTheDataBack(SimdLevel level)
    {
        var rnd = new Random(30 + (int)level);
        foreach (int blocks in new[] { 0, 1, 5 })
        {
            var data = TestData.RandomBytes(rnd, blocks * Gf16Kernels.PreparedBlock(level));
            var copy = (byte[])data.Clone();
            fixed (byte* p = copy)
            {
                Gf16Kernels.Prepare(level, p, copy.Length);
                Gf16Kernels.Finish(level, p, copy.Length);
            }

            CollectionAssert.AreEqual(data, copy, $"{level}, {blocks} blocks");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void MulAddPrepared_MatchesTheNormalKernel(SimdLevel level)
    {
        var rnd = new Random(40 + (int)level);
        const int length = 384; // three blocks of the widest layout (AVX-512: 128 bytes)
        foreach (IGf16Multiplier field in new IGf16Multiplier[] { GaloisField16.Par2, LeopardField.FF16 })
        {
            foreach (int count in new[] { 1, 3 })
            {
                foreach (bool overwrite in new[] { false, true })
                {
                    var sources = Enumerable.Range(0, count).Select(_ => TestData.RandomBytes(rnd, length)).ToArray();
                    var coefficients = Enumerable.Range(0, count).Select(i => i == 1 ? (ushort)0 : (ushort)rnd.Next(1, 65536)).ToArray();
                    var dst = TestData.RandomBytes(rnd, length);

                    var expected = (byte[])dst.Clone();
                    using (var tables = Gf16MulTables.Create(field, coefficients, level))
                    using (var pins = new PinnedArrays(sources))
                    {
                        fixed (byte* d = expected)
                        {
                            Gf16Kernels.MulAddMulti(tables, 0, count, pins.Pointers, d, length, overwrite);
                        }
                    }

                    var prepared = sources.Select(s => (byte[])s.Clone()).ToArray();
                    var actual = (byte[])dst.Clone();
                    using (var pins = new PinnedArrays(prepared))
                    {
                        fixed (byte* d = actual)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                Gf16Kernels.Prepare(level, pins.Pointers[i], length);
                            }

                            Gf16Kernels.Prepare(level, d, length);
                            Gf16Kernels.MulAddPrepared(field, level, coefficients, pins.Pointers, d, length, overwrite);
                            Gf16Kernels.Finish(level, d, length);
                        }
                    }

                    CollectionAssert.AreEqual(expected, actual, $"{field} {level} count={count} overwrite={overwrite}");
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void MulAddPrepared_NoSources_ClearsOnlyWhenOverwriting(SimdLevel level)
    {
        int length = Gf16Kernels.PreparedBlock(level);
        var dst = Enumerable.Repeat((byte)7, length).ToArray();
        fixed (byte* d = dst)
        {
            Gf16Kernels.MulAddPrepared(GaloisField16.Par2, level, [], null, d, length);
            Assert.IsTrue(dst.All(b => b == 7));
            Gf16Kernels.MulAddPrepared(GaloisField16.Par2, level, [], null, d, length, overwrite: true);
            Assert.IsTrue(dst.All(b => b == 0));
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void PreparedLengths_MustBeWholeBlocks(SimdLevel level)
    {
        int length = Gf16Kernels.PreparedBlock(level) + 1; // blocks are even, so this never is a whole number of them
        var data = new byte[length];
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            fixed (byte* p = data)
            {
                Gf16Kernels.Prepare(level, p, length);
            }
        });
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            fixed (byte* p = data)
            {
                Gf16Kernels.Finish(level, p, length);
            }
        });
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            fixed (byte* p = data)
            {
                Gf16Kernels.MulAddPrepared(GaloisField16.Par2, level, [1], &p, p, length);
            }
        });
    }

    [TestMethod]
    public void PreparedBlock_UnknownLevel_Throws()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Gf16Kernels.PreparedBlock((SimdLevel)99));

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void Gf16Field_PreparedKernel_UsesTheCurrentLevel(SimdLevel level)
    {
        Gf16Kernels.Level = level;
        var field = new Gf16Field(GaloisField16.Par2);
        var kernel = field.PreparedKernel();
        Gf16Kernels.Level = SimdLevel.Scalar; // the kernel keeps its own level
        Assert.AreEqual(Gf16Kernels.PreparedBlock(level), kernel.Block);
        var rnd = new Random(50);
        int length = 2 * Gf16Kernels.PreparedBlock(SimdLevel.Avx512);
        var a = TestData.RandomBytes(rnd, length);
        var b = TestData.RandomBytes(rnd, length);
        var expected = new byte[length];
        Gf16Kernels.MulAdd(GaloisField16.Par2, 0x1234, a, expected);
        Gf16Kernels.MulAdd(GaloisField16.Par2, 0xFEDC, b, expected);
        var dst = new byte[length];
        using (var pins = new PinnedArrays([a, b]))
        {
            fixed (byte* d = dst)
            {
                kernel.Prepare(pins.Pointers[0], length);
                kernel.Prepare(pins.Pointers[1], length);
                kernel.MulAddMulti([0x1234, 0xFEDC], pins.Pointers, d, length, overwrite: true);
                kernel.Finish(d, length);
            }
        }

        CollectionAssert.AreEqual(expected, dst, level.ToString());
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void Gf8Field_PreparedKernel_KeepsTheNormalLayout(SimdLevel level)
    {
        Gf8Kernels.Level = level;
        var field = new Gf8Field(GaloisField8.Default);
        var kernel = field.PreparedKernel();
        Assert.AreEqual(1, kernel.Block);
        var rnd = new Random(60);
        const int length = 333;
        var sources = Enumerable.Range(0, 20).Select(_ => TestData.RandomBytes(rnd, length)).ToArray(); // two groups of 16
        var coefficients = Enumerable.Range(0, 20).Select(_ => (byte)rnd.Next(256)).ToArray();
        var expected = TestData.RandomBytes(rnd, length);
        var dst = (byte[])expected.Clone();
        for (int i = 0; i < sources.Length; i++)
        {
            Gf8Kernels.MulAdd(GaloisField8.Default, coefficients[i], sources[i], expected);
        }

        using (var pins = new PinnedArrays(sources))
        {
            fixed (byte* d = dst)
            {
                var before = (byte[])sources[0].Clone();
                kernel.Prepare(pins.Pointers[0], length);
                CollectionAssert.AreEqual(before, sources[0], "nothing to prepare");
                kernel.MulAddMulti(coefficients, pins.Pointers, d, length, overwrite: false);
                kernel.Finish(d, length);
            }
        }

        CollectionAssert.AreEqual(expected, dst, level.ToString());

        var cleared = Enumerable.Repeat((byte)9, 10).ToArray();
        fixed (byte* d = cleared)
        {
            kernel.MulAddMulti([], null, d, cleared.Length, overwrite: false);
            Assert.IsTrue(cleared.All(x => x == 9));
            kernel.MulAddMulti([], null, d, cleared.Length, overwrite: true);
            Assert.IsTrue(cleared.All(x => x == 0));
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void Tables_SetSeveral_EqualsCreatingThem(SimdLevel level)
    {
        var rnd = new Random(70);
        ushort[] first = [1, 2, 3, 4];
        ushort[] replacement = [(ushort)rnd.Next(65536), (ushort)rnd.Next(65536)];
        using var tables = Gf16MulTables.Create(GaloisField16.Par2, first, level);
        tables.Set(1, replacement);
        using var expected = Gf16MulTables.Create(GaloisField16.Par2, [1, replacement[0], replacement[1], 4], level);
        Assert.IsTrue(new ReadOnlySpan<byte>(tables.Ptr, 4 * tables.Stride).SequenceEqual(new ReadOnlySpan<byte>(expected.Ptr, 4 * expected.Stride)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tables.Set(-1, [1]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tables.Set(3, [1, 2]));

        byte[] first8 = [1, 2, 3];
        using var tables8 = Gf8MulTables.Create(GaloisField8.Default, first8, level);
        tables8.Set(2, [0xC3]);
        using var expected8 = Gf8MulTables.Create(GaloisField8.Default, [1, 2, 0xC3], level);
        Assert.IsTrue(new ReadOnlySpan<byte>(tables8.Ptr, 3 * tables8.Stride).SequenceEqual(new ReadOnlySpan<byte>(expected8.Ptr, 3 * expected8.Stride)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tables8.Set(-1, [1]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tables8.Set(2, [1, 2]));
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void TablesFromTheBasis_EqualTheTablesBuiltTheSlowWay(SimdLevel level)
    {
        // Every table layout is linear in the coefficient, so XOR-ing the basis rows of its nibbles must give exactly
        // what BuildTable computes from the field's multiplication.
        int stride = Gf16Kernels.TableStride(level);
        var slow = new byte[stride];
        var rnd = new Random(80);
        foreach (IGf16Multiplier field in new IGf16Multiplier[] { GaloisField16.Par2, LeopardField.FF16 })
        {
            foreach (ushort c in new ushort[] { 0, 1, 0xFFFF, 0x8000, (ushort)rnd.Next(65536), (ushort)rnd.Next(65536) })
            {
                using var fast = Gf16MulTables.Create(field, [c], level);
                fixed (byte* s = slow)
                {
                    Gf16Kernels.BuildTable(field, c, level, s);
                }

                Assert.IsTrue(new ReadOnlySpan<byte>(fast.Ptr, stride).SequenceEqual(slow), $"{field} {level} c={c:X4}");
            }
        }

        int stride8 = Gf8Kernels.TableStride(level);
        var slow8 = new byte[stride8];
        for (int c = 0; c < 256; c += 17)
        {
            using var fast8 = Gf8MulTables.Create(GaloisField8.Default, [(byte)c], level);
            fixed (byte* s = slow8)
            {
                Gf8Kernels.BuildTable(GaloisField8.Default, (byte)c, level, s);
            }

            Assert.IsTrue(new ReadOnlySpan<byte>(fast8.Ptr, stride8).SequenceEqual(slow8), $"GF(2^8) {level} c={c:X2}");
        }
    }
}

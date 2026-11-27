using DeveParTools.Core.Galois;

namespace DeveParTools.Tests.Galois;

[TestClass]
public sealed unsafe class GaloisKernelTests
{
    public static IEnumerable<object[]> Levels => SimdSupport.SupportedLevels.Select(l => new object[] { l });

    [TestMethod]
    public void Field16_InverseAndPowConsistent()
    {
        var f = GaloisField16.Par2;
        var rnd = new Random(1);
        for (int i = 0; i < 10000; i++)
        {
            var a = (ushort)rnd.Next(1, 65536);
            var b = (ushort)rnd.Next(1, 65536);
            Assert.AreEqual(a, f.Mul(f.Div(a, b), b));
            Assert.AreEqual((ushort)1, f.Mul(a, f.Inv(a)));
            Assert.AreEqual(f.Mul(a, f.Mul(a, a)), f.Pow(a, 3));
        }
    }

    [TestMethod]
    public void Field16_MatchesCarrylessReference()
    {
        var f = GaloisField16.Par2;
        var rnd = new Random(2);
        for (int i = 0; i < 20000; i++)
        {
            var a = (ushort)rnd.Next(0, 65536);
            var b = (ushort)rnd.Next(0, 65536);
            Assert.AreEqual(ReferenceMul16(a, b, 0x1100B), f.Mul(a, b));
        }
    }

    [TestMethod]
    public void Field8_MatchesCarrylessReference()
    {
        var f = GaloisField8.Default;
        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
            {
                Assert.AreEqual((byte)ReferenceMul(a, b, 0x11D, 8), f.Mul((byte)a, (byte)b));
            }
        }
    }

    [TestMethod]
    public void Field16_RejectsNonPrimitivePolynomial()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GaloisField16.Get(0x10001));
    }

    [TestMethod]
    public void Gf16_AffineMatricesMatchMultiplication()
    {
        var f = GaloisField16.Par2;
        var rnd = new Random(3);
        for (int k = 0; k < 50; k++)
        {
            var c = (ushort)rnd.Next(1, 65536);
            Gf16Kernels.GetAffineMatrices(f, c, out var ll, out var hl, out var lh, out var hh);
            for (int t = 0; t < 200; t++)
            {
                var x = (ushort)rnd.Next(0, 65536);
                byte lo = (byte)x, hi = (byte)(x >> 8);
                int pl = Gf16Kernels.AffineEmulate(lo, ll) ^ Gf16Kernels.AffineEmulate(hi, hl);
                int ph = Gf16Kernels.AffineEmulate(lo, lh) ^ Gf16Kernels.AffineEmulate(hi, hh);
                Assert.AreEqual(f.Mul(c, x), (ushort)(pl | (ph << 8)));
            }
        }
    }

    [TestMethod]
    public void Gf8_AffineMatrixMatchesMultiplication()
    {
        var f = GaloisField8.Default;
        for (int c = 0; c < 256; c++)
        {
            var m = Gf8Kernels.GetAffineMatrix(f, (byte)c);
            for (int x = 0; x < 256; x++)
            {
                Assert.AreEqual(f.Mul((byte)c, (byte)x), Gf16Kernels.AffineEmulate((byte)x, m));
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void Gf16_MulAddMulti_MatchesReference(SimdLevel level)
    {
        var f = GaloisField16.Get(0x1002D);
        var rnd = new Random(4 + (int)level);
        byte** ptrs = stackalloc byte*[8];
        foreach (int length in new[] { 2, 30, 64, 66, 126, 128, 130, 1000, 4096, 4098 })
        {
            foreach (int count in new[] { 1, 2, 5 })
            {
                foreach (bool overwrite in new[] { false, true })
                {
                    var coefs = Enumerable.Range(0, count).Select(_ => (ushort)rnd.Next(0, 65536)).ToArray();
                    var srcs = Enumerable.Range(0, count).Select(_ => RandomBytes(rnd, length)).ToArray();
                    var dst = RandomBytes(rnd, length);
                    var expected = (byte[])dst.Clone();
                    for (int p = 0; p < length; p += 2)
                    {
                        int acc = overwrite ? 0 : expected[p] | (expected[p + 1] << 8);
                        for (int s = 0; s < count; s++)
                        {
                            acc ^= f.Mul(coefs[s], (ushort)(srcs[s][p] | (srcs[s][p + 1] << 8)));
                        }

                        expected[p] = (byte)acc;
                        expected[p + 1] = (byte)(acc >> 8);
                    }

                    using var tables = Gf16MulTables.Create(f, coefs, level);
                    var handles = srcs.Select(s => System.Runtime.InteropServices.GCHandle.Alloc(s, System.Runtime.InteropServices.GCHandleType.Pinned)).ToArray();
                    try
                    {
                        for (int s = 0; s < count; s++)
                        {
                            ptrs[s] = (byte*)handles[s].AddrOfPinnedObject();
                        }

                        fixed (byte* d = dst)
                        {
                            Gf16Kernels.MulAddMulti(tables, 0, count, ptrs, d, length, overwrite);
                        }
                    }
                    finally
                    {
                        foreach (var h in handles)
                        {
                            h.Free();
                        }
                    }

                    CollectionAssert.AreEqual(expected, dst, $"level={level} length={length} count={count} overwrite={overwrite}");
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void Gf8_MulAddMulti_MatchesReference(SimdLevel level)
    {
        var f = GaloisField8.Default;
        var rnd = new Random(40 + (int)level);
        byte** ptrs = stackalloc byte*[8];
        foreach (int length in new[] { 1, 15, 16, 17, 63, 64, 65, 129, 1000 })
        {
            foreach (int count in new[] { 1, 3 })
            {
                foreach (bool overwrite in new[] { false, true })
                {
                    var coefs = Enumerable.Range(0, count).Select(_ => (byte)rnd.Next(0, 256)).ToArray();
                    var srcs = Enumerable.Range(0, count).Select(_ => RandomBytes(rnd, length)).ToArray();
                    var dst = RandomBytes(rnd, length);
                    var expected = (byte[])dst.Clone();
                    for (int p = 0; p < length; p++)
                    {
                        int acc = overwrite ? 0 : expected[p];
                        for (int s = 0; s < count; s++)
                        {
                            acc ^= f.Mul(coefs[s], srcs[s][p]);
                        }

                        expected[p] = (byte)acc;
                    }

                    using var tables = Gf8MulTables.Create(f, coefs, level);
                    var handles = srcs.Select(s => System.Runtime.InteropServices.GCHandle.Alloc(s, System.Runtime.InteropServices.GCHandleType.Pinned)).ToArray();
                    try
                    {
                        for (int s = 0; s < count; s++)
                        {
                            ptrs[s] = (byte*)handles[s].AddrOfPinnedObject();
                        }

                        fixed (byte* d = dst)
                        {
                            Gf8Kernels.MulAddMulti(tables, 0, count, ptrs, d, length, overwrite);
                        }
                    }
                    finally
                    {
                        foreach (var h in handles)
                        {
                            h.Free();
                        }
                    }

                    CollectionAssert.AreEqual(expected, dst, $"level={level} length={length} count={count} overwrite={overwrite}");
                }
            }
        }
    }

    [TestMethod]
    public void Gf16_SingleMulAdd_Works()
    {
        var f = GaloisField16.Par2;
        var src = new byte[] { 1, 0, 2, 0, 0, 1 };
        var dst = new byte[6];
        Gf16Kernels.MulAdd(f, 3, src, dst);
        CollectionAssert.AreEqual(new byte[] { 3, 0, 6, 0, 0, 3 }, dst);
    }

    private static byte[] RandomBytes(Random rnd, int length)
    {
        var b = new byte[length];
        rnd.NextBytes(b);
        return b;
    }

    private static ushort ReferenceMul16(int a, int b, int poly) => (ushort)ReferenceMul(a, b, poly, 16);

    private static int ReferenceMul(int a, int b, int poly, int bits)
    {
        int r = 0;
        while (b != 0)
        {
            if ((b & 1) != 0)
            {
                r ^= a;
            }

            b >>= 1;
            a <<= 1;
            if ((a & (1 << bits)) != 0)
            {
                a ^= poly;
            }
        }

        return r;
    }
}

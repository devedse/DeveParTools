using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Codes;

[TestClass]
public sealed class MatrixAndCombinerTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(70)]
    [DataRow(200)]
    public void Gf16_InvertTimesOriginalIsIdentity(int n)
    {
        var f = new Gf16Field(GaloisField16.Par2);
        var rnd = new Random(n);
        var m = new GfMatrix<ushort>(n, n, f);
        for (int i = 0; i < m.Data.Length; i++)
        {
            m.Data[i] = (ushort)rnd.Next(0, 65536);
        }

        var inv = m.Clone();
        Assert.IsTrue(inv.TryInvert());
        AssertIdentity(m.Multiply(inv), f);
        AssertIdentity(inv.Multiply(m), f);
    }

    [TestMethod]
    public void Gf16_SingularDetected()
    {
        var f = new Gf16Field(GaloisField16.Par2);
        var m = new GfMatrix<ushort>(3, 3, f);
        m[0, 0] = 1; m[0, 1] = 2; m[0, 2] = 3;
        m[1, 0] = 2; m[1, 1] = 4; m[1, 2] = 6; // = 2 * row0 in GF(2^16)? 2*2=4, 2*3=6 -> yes (no reduction)
        m[2, 0] = 5; m[2, 1] = 7; m[2, 2] = 9;
        Assert.IsFalse(m.TryInvert());
    }

    [TestMethod]
    public void Gf8_Invert()
    {
        var f = new Gf8Field(GaloisField8.Default);
        var rnd = new Random(8);
        // Cauchy matrices are always invertible.
        int n = 40;
        var m = new GfMatrix<byte>(n, n, f);
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                m[r, c] = f.Inv((byte)(r ^ (c + 64)));
            }
        }

        var inv = m.Clone();
        Assert.IsTrue(inv.TryInvert());
        AssertIdentity(m.Multiply(inv), f);
        _ = rnd;
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(16)]
    public void Wide_FieldAxiomsAndInvert(int size)
    {
        var gen = size switch
        {
            3 => new byte[] { 0x1B, 0x00, 0x00 }, // x^24 + x^4 + x^3 + x + 1
            4 => new byte[] { 0x8D, 0x00, 0x00, 0x00 }, // x^32 + x^7 + x^3 + x^2 + 1
            _ => new byte[] { 0x87, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, // x^128 + x^7 + x^2 + x + 1
        };
        var f = new GfWideField(size, gen);
        var rnd = new Random(size);
        for (int i = 0; i < 50; i++)
        {
            var a = f.FromUInt64((ulong)rnd.NextInt64() | 1);
            var b = f.FromUInt64((ulong)rnd.NextInt64() | 2);
            Assert.AreEqual(f.One, f.Mul(a, f.Inv(a)));
            Assert.AreEqual(f.Mul(a, b), f.Mul(b, a));
        }

        var m = new GfMatrix<UInt128>(6, 6, f);
        for (int i = 0; i < m.Data.Length; i++)
        {
            m.Data[i] = f.FromUInt64((ulong)rnd.NextInt64());
        }

        var inv = m.Clone();
        Assert.IsTrue(inv.TryInvert());
        AssertIdentity(m.Multiply(inv), f);
    }

    [TestMethod]
    public void Big_FieldInverse()
    {
        // x^192 + x^7 + x^2 + x + 1 is irreducible.
        var gen = new byte[24];
        gen[0] = 0x87;
        var f = new GfBigField(24, gen);
        var a = f.FromUInt64(0x123456789ABCDEFUL);
        Assert.AreEqual(f.One, f.Mul(a, f.Inv(a)));
    }

    [TestMethod]
    [DataRow(1, 1, 64)]
    [DataRow(5, 3, 1000)]
    [DataRow(40, 17, 4098)]
    public void Combiner_Gf16_MatchesNaive(int inputs, int outputs, int length)
    {
        var f = new Gf16Field(GaloisField16.Par2);
        var rnd = new Random(inputs * 31 + outputs);
        var inData = Enumerable.Range(0, inputs).Select(_ => { var b = new byte[length]; rnd.NextBytes(b); return b; }).ToArray();
        var coef = new GfMatrix<ushort>(outputs, inputs, f);
        for (int i = 0; i < coef.Data.Length; i++)
        {
            coef.Data[i] = (ushort)rnd.Next(0, 65536);
        }

        var outData = Enumerable.Range(0, outputs).Select(_ => new byte[length]).ToArray();
        // Small memory budget forces several slices; small batches force several batches.
        LinearCombiner.Combine(f, new MatrixCoefficients<ushort>(coef), inputs, outputs, new MemoryRegions(inData), new MemoryRegions(outData), length,
            new LinearCombineOptions { MemoryBudget = 8 * 1024, SourcesPerBatch = 4 });

        for (int j = 0; j < outputs; j++)
        {
            var expected = new byte[length];
            for (int i = 0; i < inputs; i++)
            {
                Gf16Kernels.MulAdd(GaloisField16.Par2, coef[j, i], inData[i], expected);
            }

            CollectionAssert.AreEqual(expected, outData[j], $"output {j}");
        }
    }

    [TestMethod]
    public void Combiner_Wide_MatchesNaive()
    {
        var f = new GfWideField(4, [0x8D, 0, 0, 0]);
        var rnd = new Random(77);
        int inputs = 6, outputs = 3, length = 400;
        var inData = Enumerable.Range(0, inputs).Select(_ => { var b = new byte[length]; rnd.NextBytes(b); return b; }).ToArray();
        var coef = new GfMatrix<UInt128>(outputs, inputs, f);
        for (int i = 0; i < coef.Data.Length; i++)
        {
            coef.Data[i] = f.FromUInt64((ulong)rnd.Next());
        }

        var outData = Enumerable.Range(0, outputs).Select(_ => new byte[length]).ToArray();
        LinearCombiner.Combine(f, new MatrixCoefficients<UInt128>(coef), inputs, outputs, new MemoryRegions(inData), new MemoryRegions(outData), length);
        for (int j = 0; j < outputs; j++)
        {
            for (int p = 0; p < length; p += 4)
            {
                var acc = f.Zero;
                for (int i = 0; i < inputs; i++)
                {
                    acc ^= f.Mul(coef[j, i], f.Read(inData[i].AsSpan(p, 4)));
                }

                Assert.AreEqual(acc, f.Read(outData[j].AsSpan(p, 4)));
            }
        }
    }

    private static void AssertIdentity<T>(GfMatrix<T> m, IGaloisField<T> f) where T : IEquatable<T>
    {
        for (int r = 0; r < m.Rows; r++)
        {
            for (int c = 0; c < m.Cols; c++)
            {
                Assert.AreEqual(r == c ? f.One : f.Zero, m[r, c], $"({r},{c})");
            }
        }
    }
}

[TestClass]
public sealed class GfMatrixDocumentedExampleTests
{
    [TestMethod]
    public void WorkedExampleFromDocumentation()
    {
        var f = new Gf8Field(GaloisField8.Default);
        var a = new GfMatrix<byte>(2, 2, f);
        a[0, 0] = 1; a[0, 1] = 2; a[1, 0] = 3; a[1, 1] = 4;
        Assert.IsTrue(a.TryInvert());
        CollectionAssert.AreEqual(new byte[] { 2, 1, 0x8F, 0x8E }, a.Data);
    }

    [TestMethod]
    public void PivotingExampleFromDocumentation()
    {
        var f = new Gf8Field(GaloisField8.Default);
        var a = new GfMatrix<byte>(2, 2, f);
        a[0, 1] = 1; a[1, 0] = 1;
        Assert.IsTrue(a.TryInvert());
        CollectionAssert.AreEqual(new byte[] { 0, 1, 1, 0 }, a.Data);
    }

    [TestMethod]
    public void NonSquareCannotBeInverted()
    {
        var f = new Gf8Field(GaloisField8.Default);
        Assert.ThrowsExactly<InvalidOperationException>(() => new GfMatrix<byte>(2, 3, f).TryInvert());
    }

    [TestMethod]
    public void SelectRowsAndColumns()
    {
        var f = new Gf8Field(GaloisField8.Default);
        var a = new GfMatrix<byte>(3, 3, f);
        for (int i = 0; i < 9; i++)
        {
            a.Data[i] = (byte)i;
        }

        CollectionAssert.AreEqual(new byte[] { 6, 7, 8, 0, 1, 2 }, a.SelectRows([2, 0]).Data);
        CollectionAssert.AreEqual(new byte[] { 2, 0, 5, 3, 8, 6 }, a.SelectColumns([2, 0]).Data);
        Assert.ThrowsExactly<ArgumentException>(() => a.Multiply(new GfMatrix<byte>(2, 2, f)));
    }
}

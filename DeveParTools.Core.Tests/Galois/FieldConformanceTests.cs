using System.Numerics;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Galois;

/// <summary>
/// Every <see cref="IGaloisField{T}"/> implementation is checked against one independent reference: elements are
/// polynomials over GF(2) (a <see cref="BigInteger"/> bit pattern), multiplied carry-less and reduced modulo
/// <c>x^bits + generator</c>.
/// </summary>
[TestClass]
public sealed unsafe class FieldConformanceTests
{
    // Irreducible generators (without the leading term), little-endian, checked with Rabin's irreducibility test.
    private static readonly byte[] Gen24 = [0x1B, 0, 0]; // x^24 + x^4 + x^3 + x + 1
    private static readonly byte[] Gen128 = [0x87, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]; // x^128 + x^7 + x^2 + x + 1
    private static readonly byte[] Gen136 = [0x07, 0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]; // x^136 + x^11 + x^2 + x + 1

    [TestMethod]
    public void Gf8Field_Conforms()
    {
        var f = new Gf8Field(GaloisField8.Default);
        Assert.AreSame(GaloisField8.Default, f.Inner);
        Conformance.Check(f, [0x1D], new Random(1));
    }

    [TestMethod]
    public void Gf16Field_Conforms()
    {
        var f = new Gf16Field(GaloisField16.Par2);
        Assert.AreSame(GaloisField16.Par2, f.Inner);
        Conformance.Check(f, [0x0B, 0x10], new Random(2));
    }

    [TestMethod]
    [DataRow(1, new byte[] { 0x1D })]
    [DataRow(2, new byte[] { 0x0B, 0x10 })]
    [DataRow(3, new byte[] { 0x1B, 0, 0 })]
    [DataRow(4, new byte[] { 0x8D, 0, 0, 0 })] // x^32 + x^7 + x^3 + x^2 + 1
    public void GfWideField_Conforms(int size, byte[] generator)
        => Conformance.Check(new GfWideField(size, generator), generator, new Random(10 + size));

    [TestMethod]
    public void GfWideField_FullWidth128Bits_Conforms() => Conformance.Check(new GfWideField(16, Gen128), Gen128, new Random(16));

    [TestMethod]
    public void GfBigField_SmallSizes_Conform()
    {
        Conformance.Check(new GfBigField(1, [0x1D]), [0x1D], new Random(21));
        Conformance.Check(new GfBigField(3, Gen24), Gen24, new Random(23));
    }

    [TestMethod]
    public void GfBigField_BeyondUInt128_Conforms() => Conformance.Check(new GfBigField(17, Gen136), Gen136, new Random(37), tableCoefficients: 2, samples: 6);

    [TestMethod]
    public void WideAndBigFields_AgreeWithTheTableFields()
    {
        // Same field, three implementations: GF(2^8)/0x11D and GF(2^16)/0x1100B.
        var rnd = new Random(5);
        var w8 = new GfWideField(1, [0x1D]);
        var b16 = new GfBigField(2, [0x0B, 0x10]);
        var w16 = new GfWideField(2, [0x0B, 0x10]);
        for (int i = 0; i < 2000; i++)
        {
            byte a8 = (byte)rnd.Next(256), c8 = (byte)rnd.Next(256);
            Assert.AreEqual((UInt128)GaloisField8.Default.Mul(a8, c8), w8.Mul(a8, c8));
            ushort a = (ushort)rnd.Next(65536), c = (ushort)rnd.Next(65536);
            ushort expected = GaloisField16.Par2.Mul(a, c);
            Assert.AreEqual((UInt128)expected, w16.Mul(a, c));
            var big = b16.Mul(b16.FromUInt64(a), b16.FromUInt64(c));
            CollectionAssert.AreEqual(new[] { (byte)expected, (byte)(expected >> 8) }, big.Bytes.ToArray());
        }
    }

    [TestMethod]
    public void Constructors_ValidateSizeAndGenerator()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GfWideField(0, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GfWideField(17, new byte[17]));
        Assert.ThrowsExactly<ArgumentException>(() => new GfWideField(2, [1]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GfBigField(0, []));
        Assert.ThrowsExactly<ArgumentException>(() => new GfBigField(3, [1, 2]));
    }

    [TestMethod]
    public void GfWideField_FromUInt64_TruncatesToTheField()
    {
        Assert.AreEqual((UInt128)0x5634, new GfWideField(2, [0x0B, 0x10]).FromUInt64(0x123456789A_BC_5634));
        Assert.AreEqual((UInt128)0xFFFFFFFFFFFFFFFFUL, new GfWideField(16, Gen128).FromUInt64(ulong.MaxValue));
    }

    [TestMethod]
    public void GfBigField_FromUInt64_TruncatesOrZeroExtends()
    {
        CollectionAssert.AreEqual(new byte[] { 0x34, 0x12 }, new GfBigField(2, [0x0B, 0x10]).FromUInt64(0xAB1234).Bytes.ToArray());
        var wide = new GfBigField(17, Gen136).FromUInt64(0x0102030405060708UL).Bytes.ToArray();
        CollectionAssert.AreEqual(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, wide);
    }

    [TestMethod]
    public void GfBigElement_EqualityAndHashing()
    {
        var a = new GfBigElement([1, 2, 3]);
        var b = new GfBigElement([1, 2, 3]);
        var c = new GfBigElement([1, 2, 4]);
        Assert.IsTrue(a == b);
        Assert.IsFalse(a != b);
        Assert.IsTrue(a != c);
        Assert.IsTrue(a.Equals((object)b));
        Assert.IsFalse(a.Equals((object)c));
        Assert.IsFalse(a.Equals("not an element"));
        Assert.IsFalse(a.Equals(null));
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());

        // default(GfBigElement) has no bytes; it equals any other empty element.
        Assert.IsTrue(default(GfBigElement).Bytes.IsEmpty);
        Assert.AreEqual(0, default(GfBigElement).Raw.Length);
        Assert.AreEqual(default, new GfBigElement([]));
        Assert.AreEqual(default(GfBigElement).GetHashCode(), new GfBigElement([]).GetHashCode());
    }

    /// <summary>The checks, generic over the element type.</summary>
    private static class Conformance
    {
        public static void Check<T>(IGaloisField<T> f, byte[] generator, Random rnd, int tableCoefficients = 4, int samples = 40)
            where T : IEquatable<T>
        {
            int n = f.SymbolSize;
            int bits = 8 * n;
            Assert.AreEqual(generator.Length, n);
            CollectionAssert.AreEqual(generator, f.GeneratorBytes);
            var modulus = (BigInteger.One << bits) | new BigInteger(generator, isUnsigned: true);

            T Elem(BigInteger v) => f.Read(ToBytes(v, n));
            BigInteger Value(T e)
            {
                var b = new byte[n];
                f.Write(e, b);
                return new BigInteger(b, isUnsigned: true);
            }

            BigInteger RandomValue() => new(TestData.RandomBytes(rnd, n), isUnsigned: true);

            Assert.AreEqual(BigInteger.Zero, Value(f.Zero));
            Assert.AreEqual(BigInteger.One, Value(f.One));
            Assert.IsTrue(f.IsZero(f.Zero));
            Assert.IsFalse(f.IsZero(f.One));
            Assert.AreEqual(f.One, f.FromUInt64(1));
            Assert.AreEqual(f.Zero, f.FromUInt64(0));

            // Read takes the first SymbolSize bytes; Write writes exactly SymbolSize bytes.
            var longer = TestData.RandomBytes(rnd, n + 3);
            var written = new byte[n + 3];
            f.Write(f.Read(longer), written);
            CollectionAssert.AreEqual(longer[..n], written[..n]);
            CollectionAssert.AreEqual(new byte[3], written[n..]);

            for (int i = 0; i < samples; i++)
            {
                var av = RandomValue();
                var bv = RandomValue();
                T a = Elem(av), b = Elem(bv);
                Assert.AreEqual(av ^ bv, Value(f.Add(a, b)), "add is xor");
                Assert.AreEqual(ReferenceMul(av, bv, modulus, bits), Value(f.Mul(a, b)), "mul");
                Assert.AreEqual(f.Zero, f.Mul(a, f.Zero));
                Assert.AreEqual(a, f.Mul(a, f.One));
                if (!f.IsZero(b))
                {
                    var inv = f.Inv(b);
                    Assert.AreEqual(f.One, f.Mul(b, inv), "b · b⁻¹ = 1");
                    Assert.AreEqual(a, f.Mul(f.Div(a, b), b), "(a / b) · b = a");
                }
            }

            Assert.ThrowsExactly<DivideByZeroException>(() => f.Inv(f.Zero));

            // Row operations against element-wise reference values (zeros included, they are special-cased).
            int len = 9;
            var dst = Enumerable.Range(0, len).Select(_ => RandomValue()).ToArray();
            var src = Enumerable.Range(0, len).Select(i => i % 3 == 0 ? BigInteger.Zero : RandomValue()).ToArray();
            var cv = RandomValue() | 1;
            var row = dst.Select(Elem).ToArray();
            f.RowMulAdd(row, src.Select(Elem).ToArray(), Elem(cv));
            for (int i = 0; i < len; i++)
            {
                Assert.AreEqual(dst[i] ^ ReferenceMul(cv, src[i], modulus, bits), Value(row[i]), $"RowMulAdd {i}");
            }

            var unchanged = dst.Select(Elem).ToArray();
            f.RowMulAdd(unchanged, src.Select(Elem).ToArray(), f.Zero);
            CollectionAssert.AreEqual(dst, unchanged.Select(Value).ToArray(), "RowMulAdd by zero");

            var scaled = src.Select(Elem).ToArray();
            f.RowScale(scaled, Elem(cv));
            CollectionAssert.AreEqual(src.Select(s => ReferenceMul(cv, s, modulus, bits)).ToArray(), scaled.Select(Value).ToArray(), "RowScale");

            CheckRegions(f, rnd, modulus, bits, tableCoefficients);
        }

        /// <summary>MulAddMulti over byte regions: <c>dst (^)= Σ coef[first + i] · src[i]</c>.</summary>
        private static void CheckRegions<T>(IGaloisField<T> f, Random rnd, BigInteger modulus, int bits, int coefficientCount)
            where T : IEquatable<T>
        {
            int n = f.SymbolSize;
            int symbols = 37;
            int length = symbols * n;
            var coefs = Enumerable.Range(0, coefficientCount).Select(i => i == 2 ? BigInteger.Zero : new BigInteger(TestData.RandomBytes(rnd, n), isUnsigned: true)).ToArray();
            using var tables = f.CreateTables(coefs.Select(c => f.Read(ToBytes(c, n))).ToArray());
            Assert.AreEqual(coefficientCount, tables.Count);
            int first = 1;
            int count = coefficientCount - first;
            var srcs = Enumerable.Range(0, count).Select(_ => TestData.RandomBytes(rnd, length)).ToArray();
            foreach (bool overwrite in new[] { false, true })
            {
                var dst = TestData.RandomBytes(rnd, length);
                var expected = new byte[length];
                for (int s = 0; s < symbols; s++)
                {
                    var acc = overwrite ? BigInteger.Zero : new BigInteger(dst.AsSpan(s * n, n), isUnsigned: true);
                    for (int i = 0; i < count; i++)
                    {
                        acc ^= ReferenceMul(coefs[first + i], new BigInteger(srcs[i].AsSpan(s * n, n), isUnsigned: true), modulus, bits);
                    }

                    ToBytes(acc, n).CopyTo(expected, s * n);
                }

                using (var pins = new PinnedArrays(srcs))
                {
                    fixed (byte* d = dst)
                    {
                        f.MulAddMulti(tables, first, count, pins.Pointers, d, length, overwrite);
                    }
                }

                CollectionAssert.AreEqual(expected, dst, $"MulAddMulti overwrite={overwrite}");
            }
        }

        private static BigInteger ReferenceMul(BigInteger a, BigInteger b, BigInteger modulus, int bits)
        {
            BigInteger product = 0;
            for (int i = 0; b >> i != 0; i++)
            {
                if (!((b >> i) & 1).IsZero)
                {
                    product ^= a << i;
                }
            }

            for (long d = (long)product.GetBitLength() - 1; d >= bits; d--)
            {
                if (!((product >> (int)d) & 1).IsZero)
                {
                    product ^= modulus << (int)(d - bits);
                }
            }

            return product;
        }

        private static byte[] ToBytes(BigInteger v, int n)
        {
            var b = new byte[n];
            v.TryWriteBytes(b, out _, isUnsigned: true);
            return b;
        }
    }
}

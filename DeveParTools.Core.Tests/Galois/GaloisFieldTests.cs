using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Galois;

/// <summary>The scalar log/exp-table fields <see cref="GaloisField8"/> and <see cref="GaloisField16"/>.</summary>
[TestClass]
public sealed class GaloisFieldTests
{
    [TestMethod]
    public void Field8_DefaultIsCachedAndUsesPar3Generator()
    {
        Assert.AreSame(GaloisField8.Default, GaloisField8.Get(0x11D));
        Assert.AreEqual(0x11D, GaloisField8.Default.Polynomial);
        Assert.AreEqual(256, GaloisField8.Order);
        Assert.AreEqual(255, GaloisField8.Limit);
    }

    [TestMethod]
    public void Field16_Par2IsCachedAndUsesPar2Generator()
    {
        Assert.AreSame(GaloisField16.Par2, GaloisField16.Get(0x1100B));
        Assert.AreEqual(0x1100B, GaloisField16.Par2.Polynomial);
        Assert.AreEqual(65536, GaloisField16.Order);
        Assert.AreEqual(65535, GaloisField16.Limit);
    }

    [TestMethod]
    [DataRow(0x1D, DisplayName = "degree too low")]
    [DataRow(0x21D, DisplayName = "degree too high")]
    [DataRow(0x101, DisplayName = "x^8 + 1: x has order 8")]
    [DataRow(0x100, DisplayName = "x^8: x is not invertible, never returns to 1")]
    [DataRow(0x11B, DisplayName = "AES polynomial: irreducible but x is not a generator")]
    public void Field8_RejectsBadPolynomials(int polynomial)
        => Assert.ThrowsExactly<ArgumentException>(() => GaloisField8.Get(polynomial));

    [TestMethod]
    [DataRow(0x100B, DisplayName = "degree too low")]
    [DataRow(0x2100B, DisplayName = "degree too high")]
    [DataRow(0x10001, DisplayName = "x^16 + 1: x has order 16")]
    [DataRow(0x10000, DisplayName = "x^16: x is not invertible, never returns to 1")]
    public void Field16_RejectsBadPolynomials(int polynomial)
        => Assert.ThrowsExactly<ArgumentException>(() => GaloisField16.Get(polynomial));

    [TestMethod]
    public void Field8_DivInvPowExpLog_AgreeWithMultiplication()
    {
        var f = GaloisField8.Default;
        for (int a = 0; a < 256; a++)
        {
            Assert.AreEqual(0, f.Mul((byte)a, 0));
            Assert.AreEqual(0, f.Mul(0, (byte)a));
            Assert.AreEqual(1, f.Pow((byte)a, 0), "a^0 = 1, also for a = 0");
            if (a == 0)
            {
                Assert.AreEqual(0, f.Div(0, 7));
                Assert.AreEqual(0, f.Pow(0, 5));
                continue;
            }

            Assert.AreEqual(1, f.Mul((byte)a, f.Inv((byte)a)));
            Assert.AreEqual(f.Exp(f.Log((byte)a)), (byte)a);
            Assert.AreEqual(f.Mul((byte)a, f.Mul((byte)a, (byte)a)), f.Pow((byte)a, 3));
            Assert.AreEqual((byte)a, f.Pow((byte)a, 256), "a^(2^8) = a (Frobenius)");
            for (int b = 1; b < 256; b += 17)
            {
                Assert.AreEqual((byte)a, f.Mul(f.Div((byte)a, (byte)b), (byte)b));
            }
        }

        // alpha = x = 2 generates the multiplicative group, and Exp is periodic with period 255 in both directions.
        Assert.AreEqual(2, f.Exp(1));
        Assert.AreEqual(f.Exp(254), f.Exp(-1));
        Assert.AreEqual(f.Exp(3), f.Exp(3 + 255 * 4));
        Assert.AreEqual(1, f.Mul(2, f.Exp(-1)));
    }

    [TestMethod]
    public void Field16_DivInvPowExpLog_EdgeCases()
    {
        var f = GaloisField16.Par2;
        Assert.AreEqual(0, f.Mul(0, 1234));
        Assert.AreEqual(0, f.Mul(1234, 0));
        Assert.AreEqual(0, f.Div(0, 1234));
        Assert.AreEqual(1, f.Pow(0, 0));
        Assert.AreEqual(0, f.Pow(0, 9));
        Assert.AreEqual(2, f.Exp(1));
        Assert.AreEqual(f.Exp(65534), f.Exp(-1));
        Assert.AreEqual(1234, f.Exp(f.Log(1234)));
        Assert.AreEqual((ushort)4321, f.Pow(4321, 65536), "a^(2^16) = a (Frobenius)");
        Assert.AreEqual((ushort)1, f.Pow(4321, 65535L * 1000), "the group order is 65535");
    }

    [TestMethod]
    public void ZeroHasNoInverseOrLogarithm()
    {
        Assert.ThrowsExactly<DivideByZeroException>(() => GaloisField8.Default.Div(1, 0));
        Assert.ThrowsExactly<DivideByZeroException>(() => GaloisField8.Default.Inv(0));
        Assert.ThrowsExactly<ArgumentException>(() => GaloisField8.Default.Log(0));
        Assert.ThrowsExactly<DivideByZeroException>(() => GaloisField16.Par2.Div(1, 0));
        Assert.ThrowsExactly<DivideByZeroException>(() => GaloisField16.Par2.Inv(0));
        Assert.ThrowsExactly<ArgumentException>(() => GaloisField16.Par2.Log(0));
    }

    [TestMethod]
    public void OtherPrimitivePolynomialsWork()
    {
        // 0x12B (8 bit) and 0x1002D (16 bit, Leopard's polynomial) are primitive too.
        var f8 = GaloisField8.Get(0x12B);
        var f16 = GaloisField16.Get(0x1002D);
        Assert.AreEqual(0x2B, f8.Mul(0x80, 2), "x^7 · x = x^8 = x^5 + x^3 + x + 1");
        Assert.AreEqual(0x2D, f16.Mul(0x8000, 2), "x^15 · x = x^16 = x^5 + x^3 + x^2 + 1");
        Assert.AreEqual(1, f8.Mul(0x80, f8.Inv(0x80)));
        Assert.AreEqual(1, f16.Mul(0x8000, f16.Inv(0x8000)));
    }
}

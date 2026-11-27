using System.Text;
using DeveParTools.Core.Hashing;

namespace DeveParTools.Core.Tests.Hashing;

[TestClass]
public sealed class CrcTests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [TestMethod]
    public void Crc64Iso_CheckValue() => Assert.AreEqual(0xB90956C775A41001UL, Crc64Iso.Compute(Check));

    [TestMethod]
    public void Crc32_CheckValue() => Assert.AreEqual(0xCBF43926u, Crc32Ieee.Compute(Check));

    [TestMethod]
    public void Crc64Iso_Continuation()
    {
        var data = RandomBytes(1000, 1);
        var whole = Crc64Iso.Compute(data);
        var part = Crc64Iso.Compute(data.AsSpan(0, 333));
        Assert.AreEqual(whole, Crc64Iso.Compute(data.AsSpan(333), part));
        Assert.AreEqual(Crc64Iso.Compute(new byte[777]), Crc64Iso.AppendZeros(0, 777));
    }

    [TestMethod]
    public void Crc32_Continuation()
    {
        var data = RandomBytes(1000, 2);
        var part = Crc32Ieee.Compute(data.AsSpan(0, 333));
        Assert.AreEqual(Crc32Ieee.Compute(data), Crc32Ieee.Compute(data.AsSpan(333), part));
        Assert.AreEqual(Crc32Ieee.Compute(new byte[777]), Crc32Ieee.AppendZeros(0, 777));
    }

    [TestMethod]
    public void Combine_AndAppendZeros_MatchDirectComputation()
    {
        var rnd = new Random(3);
        foreach (int lenA in new[] { 0, 1, 7, 100, 4096 })
        {
            foreach (int lenB in new[] { 0, 1, 3, 64, 1000, 65537 })
            {
                var a = new byte[lenA];
                var b = new byte[lenB];
                rnd.NextBytes(a);
                rnd.NextBytes(b);
                var ab = a.Concat(b).ToArray();
                Assert.AreEqual(Crc32Ieee.Compute(ab), Crc32Ieee.Combine(Crc32Ieee.Compute(a), Crc32Ieee.Compute(b), lenB), $"crc32 {lenA}+{lenB}");
                Assert.AreEqual(Crc64Iso.Compute(ab), Crc64Iso.Combine(Crc64Iso.Compute(a), Crc64Iso.Compute(b), lenB), $"crc64 {lenA}+{lenB}");
                Assert.AreEqual(Crc32Ieee.Compute(ab), Crc32Ieee.Compute(b, Crc32Ieee.Compute(a)), $"crc32 continuation {lenA}+{lenB}");

                var withZeros = a.Concat(new byte[lenB]).ToArray();
                Assert.AreEqual(Crc32Ieee.Compute(withZeros), Crc32Ieee.AppendZeros(Crc32Ieee.Compute(a), lenB), $"crc32 zeros {lenA}+{lenB}");
                Assert.AreEqual(Crc64Iso.Compute(withZeros), Crc64Iso.AppendZeros(Crc64Iso.Compute(a), lenB), $"crc64 zeros {lenA}+{lenB}");

                // The raw-register byte loop agrees too.
                Assert.AreEqual(Crc32Ieee.Compute(ab), ~Crc32Ieee.UpdateRaw(~Crc32Ieee.Compute(a), b));
            }
        }
    }

    [TestMethod]
    public void AppendZeros_HugeCounts_AreFastAndConsistent()
    {
        // 2^40 zero bytes cannot be hashed directly, but splitting the count must agree with doing it at once.
        const long huge = 1L << 40;
        Assert.AreEqual(Crc64Iso.AppendZeros(Crc64Iso.AppendZeros(12345, huge), 999), Crc64Iso.AppendZeros(12345, huge + 999));
        Assert.AreEqual(Crc32Ieee.AppendZeros(Crc32Ieee.AppendZeros(54321, huge), 999), Crc32Ieee.AppendZeros(54321, huge + 999));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Crc32Ieee.AppendZeros(0, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Crc64Iso.AppendZeros(0, -1));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(40)]
    [DataRow(1024)]
    public void RollingCrc64_MatchesDirect(int window)
    {
        var data = RandomBytes(window + 3000, 3);
        var roll = new RollingCrc64(window);
        Assert.AreEqual(window, roll.Window);
        ulong crc = Crc64Iso.Compute(data.AsSpan(0, window));
        for (int i = 0; i + window < data.Length; i++)
        {
            crc = roll.Slide(crc, data[i], data[i + window]);
            Assert.AreEqual(Crc64Iso.Compute(data.AsSpan(i + 1, window)), crc, $"i={i}");
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(1024)]
    public void RollingCrc32_MatchesDirect(int window)
    {
        var data = RandomBytes(window + 3000, 4);
        var roll = new RollingCrc32(window);
        Assert.AreEqual(window, roll.Window);
        uint crc = Crc32Ieee.Compute(data.AsSpan(0, window));
        for (int i = 0; i + window < data.Length; i++)
        {
            crc = roll.Slide(crc, data[i], data[i + window]);
            Assert.AreEqual(Crc32Ieee.Compute(data.AsSpan(i + 1, window)), crc, $"i={i}");
        }
    }

    private static byte[] RandomBytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }
}

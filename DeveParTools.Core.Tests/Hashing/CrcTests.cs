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
    [DataRow(1)]
    [DataRow(40)]
    [DataRow(1024)]
    public void RollingCrc64_MatchesDirect(int window)
    {
        var data = RandomBytes(window + 3000, 3);
        var roll = new RollingCrc64(window);
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

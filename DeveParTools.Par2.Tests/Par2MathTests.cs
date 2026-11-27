namespace DeveParTools.Par2.Tests;

[TestClass]
public sealed class Par2MathTests
{
    [TestMethod]
    public void Bases_MatchTheDocumentedTable()
    {
        // n_i: 1 2 4 7 8 11 13 14 16 19 22 (no multiples of 3, 5, 17, 257); base_i = 2^n_i in GF(2^16) / 0x1100B.
        ushort[] expected = [2, 4, 16, 128, 256, 2048, 8192, 16384, 4107, 32856, 17132];
        CollectionAssert.AreEqual(expected, Enumerable.Range(0, expected.Length).Select(Par2Math.Base).ToArray());
        Assert.AreEqual(0x1100B, Par2Math.Field.Polynomial);
    }

    [TestMethod]
    public void Bases_AreDistinct_AndHaveFullOrder()
    {
        var f = Par2Math.Field;
        var all = Enumerable.Range(0, Par2Math.MaxInputSlices).Select(Par2Math.Base).ToList();
        Assert.AreEqual(Par2Math.MaxInputSlices, all.Distinct().Count());
        foreach (var b in all)
        {
            // Order 65535 = 3·5·17·257: no proper divisor d = 65535/p gives b^d = 1.
            foreach (uint p in new uint[] { 3, 5, 17, 257 })
            {
                Assert.AreNotEqual((ushort)1, f.Pow(b, 65535 / p));
            }
        }
    }

    [TestMethod]
    public void Coefficients_FollowTheWorkedExample()
    {
        // R_0 = D0 ^ D1 ^ D2; R_1 = 2·D0 ^ 4·D1 ^ 16·D2; R_2 = 4·D0 ^ 16·D1 ^ 256·D2.
        ushort[][] expected = [[1, 1, 1], [2, 4, 16], [4, 16, 256]];
        for (uint e = 0; e < 3; e++)
        {
            CollectionAssert.AreEqual(expected[e], Enumerable.Range(0, 3).Select(i => Par2Math.Coefficient(i, e)).ToArray());
        }

        Assert.AreEqual(Par2Math.Coefficient(5, 0), Par2Math.Coefficient(5, 65535), "exponents repeat with period 65535");
    }
}

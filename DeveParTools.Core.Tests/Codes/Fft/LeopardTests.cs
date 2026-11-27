using DeveParTools.Core.Codes.Fft;

namespace DeveParTools.Core.Tests.Codes.Fft;

/// <summary>
/// <see cref="LeopardField"/> and the <see cref="LeopardCodec"/> paths not reached by the vector and round-trip tests in
/// <c>LeopardCodecTests</c>: lost recovery blocks, odd transform sizes, odd byte counts and argument checks.
/// </summary>
[TestClass]
public sealed class LeopardTests
{
    public static IEnumerable<object[]> Fields => [[LeopardField.FF8], [LeopardField.FF16]];

    [TestMethod]
    public void Fields_HaveTheLeopardParameters()
    {
        Assert.AreEqual(8, LeopardField.FF8.Bits);
        Assert.AreEqual(256, LeopardField.FF8.Order);
        Assert.AreEqual(255, LeopardField.FF8.Modulus);
        Assert.AreEqual(0x11D, LeopardField.FF8.Polynomial);
        Assert.AreEqual(1, LeopardField.FF8.SymbolSize);
        Assert.AreEqual(16, LeopardField.FF16.Bits);
        Assert.AreEqual(65536, LeopardField.FF16.Order);
        Assert.AreEqual(65535, LeopardField.FF16.Modulus);
        Assert.AreEqual(0x1002D, LeopardField.FF16.Polynomial);
        Assert.AreEqual(2, LeopardField.FF16.SymbolSize);
    }

    [TestMethod]
    [DynamicData(nameof(Fields))]
    public void CantorBasisMultiplication_IsAFieldMultiplication(LeopardField f)
    {
        var rnd = new Random(f.Bits);
        for (int i = 0; i < 3000; i++)
        {
            int a = rnd.Next(f.Order), b = rnd.Next(f.Order), c = rnd.Next(f.Order);
            Assert.AreEqual(a, f.Mul(a, 1), "β0 = 1 is the unit");
            Assert.AreEqual(0, f.Mul(a, 0));
            Assert.AreEqual(f.Mul(a, b), f.Mul(b, a));
            Assert.AreEqual(f.Mul(f.Mul(a, b), c), f.Mul(a, f.Mul(b, c)));
            // Distributive over XOR: the Cantor basis is a linear change of basis of the polynomial field.
            Assert.AreEqual(f.Mul(a, b ^ c), f.Mul(a, b) ^ f.Mul(a, c));
            if (a != 0)
            {
                Assert.AreEqual(a, f.Exp(f.Log(a)));
                Assert.AreEqual(1, f.Mul(a, f.Exp(f.Modulus - f.Log(a))), "a · a⁻¹ = 1");
            }
        }

        Assert.AreEqual(f.Modulus, f.Log(0), "log 0 is stored as Modulus (the FFT's 'multiply by 0')");
    }

    [TestMethod]
    [DynamicData(nameof(Fields))]
    public void LogArithmetic_MatchesMultiplication(LeopardField f)
    {
        var rnd = new Random(f.Bits + 1);
        for (int i = 0; i < 2000; i++)
        {
            int a = rnd.Next(f.Order), b = rnd.Next(1, f.Order), c = rnd.Next(1, f.Order);
            Assert.AreEqual(f.Mul(a, b), f.MultiplyLog(a, f.Log(b)));
            Assert.AreEqual(f.Mul(b, c), f.Exp(f.AddMod(f.Log(b), f.Log(c)) % f.Modulus));
            Assert.AreEqual(f.Log(b), f.AddMod(f.SubMod(f.Log(b), f.Log(c)), f.Log(c)) % f.Modulus);
        }

        Assert.AreEqual(0, f.MultiplyLog(0, 5));
    }

    /// <summary>The fact the FFT butterflies rely on; see the remarks on LeopardCodec.IfftDitCore.</summary>
    [TestMethod]
    [DynamicData(nameof(Fields))]
    public void FftSkew_IsModulusExactlyAtPowersOfTwoMinusOne(LeopardField f)
    {
        for (int j = 0; j < f.FftSkew.Length; j++)
        {
            bool powerOfTwoMinusOne = ((j + 1) & j) == 0;
            Assert.AreEqual(powerOfTwoMinusOne, f.FftSkew[j] == f.Modulus, $"j={j}");
        }
    }

    [TestMethod]
    [DataRow(2, 2)]
    [DataRow(8, 8)]
    [DataRow(8, 5)] // only the first 5 inputs are non-zero
    [DataRow(16, 16)]
    [DataRow(32, 32)]
    [DataRow(64, 3)]
    public void Fwht_MatchesTheDefinition(int m, int truncated)
    {
        // H[u] = Σ_v (−1)^popcount(u & v) · x[v]  (mod Modulus), with partially reduced results (Modulus ≡ 0).
        var f = LeopardField.FF8;
        var rnd = new Random(m * 100 + truncated);
        var data = new int[m];
        for (int i = 0; i < truncated; i++)
        {
            data[i] = rnd.Next(f.Modulus);
        }

        var expected = new long[m];
        for (int u = 0; u < m; u++)
        {
            long sum = 0;
            for (int v = 0; v < m; v++)
            {
                sum += (System.Numerics.BitOperations.PopCount((uint)(u & v)) & 1) == 0 ? data[v] : -data[v];
            }

            expected[u] = ((sum % f.Modulus) + f.Modulus) % f.Modulus;
        }

        f.Fwht(data, m, truncated);
        CollectionAssert.AreEqual(expected, data.Select(x => (long)(x % f.Modulus)).ToArray());
    }

    [TestMethod]
    [DataRow(10, 6, 3, 2, 64)] // missing recovery blocks are erasures too
    [DataRow(3, 2, 2, 0, 64)] // m = 2, n = 8: the IFFT has a trailing single layer
    [DataRow(20, 5, 4, 1, 32)] // m = 8, n = 32
    [DataRow(9, 9, 5, 4, 86)] // byte count not a multiple of 16 or 32 (XOR tails)
    [DataRow(300, 40, 40, 0, 2)] // GF(2^16), 1 symbol per block
    [DataRow(1, 1, 1, 0, 33)]
    [DataRow(7, 1, 1, 0, 33)]
    public void Decode_RecoversWithLostRecoveryBlocks(int k, int rc, int lostOriginals, int lostRecovery, int bytes)
    {
        var rnd = new Random(k * 31 + rc);
        var field = LeopardCodec.SelectField(k, rc);
        var orig = Enumerable.Range(0, k).Select(_ => TestData.RandomBytes(rnd, bytes)).ToArray();
        var recovery = Encode(field, orig, rc, bytes, rnd);
        var lost = Enumerable.Range(0, k).OrderBy(_ => rnd.Next()).Take(lostOriginals).ToHashSet();
        var lostRec = Enumerable.Range(0, rc).OrderBy(_ => rnd.Next()).Take(lostRecovery).ToHashSet();
        var work = Enumerable.Range(0, LeopardCodec.DecodeWorkCount(k, rc)).Select(_ => new byte[bytes]).ToArray();
        using (var po = new PinnedArrays(orig))
        using (var pr = new PinnedArrays(recovery))
        using (var pw = new PinnedArrays(work))
        {
            var o = po.Addresses;
            var r = pr.Addresses;
            foreach (int i in lost)
            {
                o[i] = 0;
            }

            foreach (int i in lostRec)
            {
                r[i] = 0;
            }

            LeopardCodec.Decode(field, o, r, pw.Addresses, bytes, threads: 2);
        }

        foreach (int i in lost)
        {
            CollectionAssert.AreEqual(orig[i], work[i], $"original {i}");
        }
    }

    [TestMethod]
    public void Decode_NothingMissing_LeavesTheWorkBuffersAlone()
    {
        var orig = new[] { new byte[64], new byte[64] };
        var rec = new[] { new byte[64], new byte[64] };
        var work = Enumerable.Range(0, LeopardCodec.DecodeWorkCount(2, 2)).Select(_ => Enumerable.Repeat((byte)7, 64).ToArray()).ToArray();
        using var po = new PinnedArrays(orig);
        using var pr = new PinnedArrays(rec);
        using var pw = new PinnedArrays(work);
        LeopardCodec.Decode(LeopardField.FF8, po.Addresses, pr.Addresses, pw.Addresses, 64);
        Assert.IsTrue(work.All(w => w.All(b => b == 7)));
    }

    [TestMethod]
    public void Decode_TooFewRecoveryBlocks_Throws()
    {
        var orig = new[] { new byte[64], new byte[64], new byte[64] };
        var rec = new[] { new byte[64], new byte[64] };
        using var po = new PinnedArrays(orig);
        using var pr = new PinnedArrays(rec);
        var o = po.Addresses;
        var r = pr.Addresses;
        o[0] = o[1] = 0;
        r[1] = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => LeopardCodec.Decode(LeopardField.FF8, o, r, new nint[8], 64));
    }

    [TestMethod]
    public void FewerRecoveryBlocksWithTheSameM_AreAPrefix()
    {
        // From the class remarks: a recovery block only depends on m = NextPow2(count) and the data.
        var rnd = new Random(4);
        var orig = Enumerable.Range(0, 12).Select(_ => TestData.RandomBytes(rnd, 128)).ToArray();
        var five = Encode(LeopardField.FF8, orig, 5, 128, rnd);
        var eight = Encode(LeopardField.FF8, orig, 8, 128, rnd);
        for (int i = 0; i < 5; i++)
        {
            CollectionAssert.AreEqual(eight[i], five[i], $"recovery {i}");
        }
    }

    [TestMethod]
    public void SingleRecoveryBlock_IsTheXorOfTheOriginals_AndSingleOriginalIsCopied()
    {
        var rnd = new Random(5);
        var orig = Enumerable.Range(0, 5).Select(_ => TestData.RandomBytes(rnd, 100)).ToArray();
        var parity = Encode(LeopardField.FF8, orig, 1, 100, rnd)[0];
        var xor = new byte[100];
        foreach (var o in orig)
        {
            for (int i = 0; i < 100; i++)
            {
                xor[i] ^= o[i];
            }
        }

        CollectionAssert.AreEqual(xor, parity);
        CollectionAssert.AreEqual(orig[0], Encode(LeopardField.FF8, orig[..1], 1, 100, rnd)[0]);
        Assert.AreEqual(1, LeopardCodec.EncodeWorkCount(1, 1));
        Assert.AreEqual(1, LeopardCodec.EncodeWorkCount(5, 1));
        Assert.AreEqual(16, LeopardCodec.EncodeWorkCount(12, 5));
        Assert.AreEqual(5, LeopardCodec.DecodeWorkCount(5, 1));
        Assert.AreEqual(1, LeopardCodec.DecodeWorkCount(1, 1));
        Assert.AreEqual(32, LeopardCodec.DecodeWorkCount(20, 5));
    }

    [TestMethod]
    public void Validate_ChecksCountsAndFieldSize()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LeopardCodec.Validate(LeopardField.FF8, 0, 1));
        Assert.ThrowsExactly<ArgumentException>(() => LeopardCodec.Validate(LeopardField.FF8, 1, 0));
        Assert.ThrowsExactly<ArgumentException>(() => LeopardCodec.Validate(LeopardField.FF16, 65000, 1000));
        LeopardCodec.Validate(LeopardField.FF8, 300, 1); // a single parity block needs no FFT, so any count works
        LeopardCodec.Validate(LeopardField.FF16, 60000, 1000);
        Assert.AreEqual(LeopardField.FF8, LeopardCodec.SelectField(128, 128));
        Assert.AreEqual(LeopardField.FF16, LeopardCodec.SelectField(129, 128));
    }

    private static byte[][] Encode(LeopardField field, byte[][] orig, int rc, int bytes, Random rnd)
    {
        var work = Enumerable.Range(0, LeopardCodec.EncodeWorkCount(orig.Length, rc)).Select(_ => TestData.RandomBytes(rnd, bytes)).ToArray();
        using (var po = new PinnedArrays(orig))
        using (var pw = new PinnedArrays(work))
        {
            LeopardCodec.Encode(field, po.Addresses, rc, pw.Addresses, bytes, threads: 3);
        }

        return work.Take(rc).ToArray();
    }
}

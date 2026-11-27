using DeveParTools.Core.Codes.Fft;

namespace DeveParTools.Core.Tests.Codes.Fft;

[TestClass]
public sealed unsafe class LeopardCodecTests
{
    /// <summary>Same data generator as the C program that produced TestData/leopard_vectors.txt.</summary>
    private static byte Data(uint i, uint j)
    {
        uint x = unchecked((i * 2654435761u) ^ (j * 40503u + 12345u));
        x ^= x >> 13;
        x = unchecked(x * 0x5bd1e995);
        x ^= x >> 15;
        return (byte)x;
    }

    [TestMethod]
    public void Encode_IsBitIdenticalToLeopardC()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "leopard_vectors.txt"));
        int li = 0;
        const int bytes = 128;
        int cases = 0;
        while (li < lines.Length)
        {
            var parts = lines[li++].Split(' ');
            int k = int.Parse(parts[0]), rc = int.Parse(parts[1]);
            var field = LeopardCodec.SelectField(k, rc);
            var orig = new byte[k][];
            for (int i = 0; i < k; i++)
            {
                orig[i] = new byte[bytes];
                for (int j = 0; j < bytes; j++)
                {
                    orig[i][j] = Data((uint)i, (uint)j);
                }
            }

            int wc = LeopardCodec.EncodeWorkCount(k, rc);
            var work = Enumerable.Range(0, wc).Select(_ => new byte[bytes]).ToArray();
            WithPointers(orig, op => WithPointers(work, wp => LeopardCodec.Encode(field, op, rc, wp, bytes, threads: 2)));
            for (int r = 0; r < rc; r++)
            {
                Assert.AreEqual(lines[li++], Convert.ToHexStringLower(work[r]), $"k={k} rc={rc} recovery {r}");
            }

            cases++;
        }

        Assert.AreEqual(9, cases);
    }

    [TestMethod]
    [DataRow(2, 1, 64)]
    [DataRow(1, 1, 64)]
    [DataRow(10, 4, 256)]
    [DataRow(100, 37, 192)]
    [DataRow(500, 200, 8192)]
    [DataRow(2000, 2000, 64)]
    public void EncodeThenDecode_RecoversMissingOriginals(int k, int rc, int bytes)
    {
        var rnd = new Random(k * 7 + rc);
        var field = LeopardCodec.SelectField(k, rc);
        var orig = Enumerable.Range(0, k).Select(_ => { var b = new byte[bytes]; rnd.NextBytes(b); return b; }).ToArray();
        var work = Enumerable.Range(0, LeopardCodec.EncodeWorkCount(k, rc)).Select(_ => new byte[bytes]).ToArray();
        var encoding = new System.Collections.Concurrent.ConcurrentBag<double>();
        WithPointers(orig, op => WithPointers(work, wp => LeopardCodec.Encode(field, op, rc, wp, bytes, threads: 4, progress: encoding.Add)));
        Assert.AreEqual(1.0, encoding.Max(), 1e-12, "every thread's part reports its transforms, up to 100%");
        Assert.IsTrue(encoding.All(f => f > 0));
        var recovery = work.Take(rc).Select(w => (byte[])w.Clone()).ToArray();

        // Lose up to rc originals (some recovery blocks lost too, as long as enough remain).
        int lose = Math.Min(k, rc);
        var lostOrig = Enumerable.Range(0, k).OrderBy(_ => rnd.Next()).Take(lose).ToHashSet();
        int dropRecovery = rc - lose;
        var lostRec = Enumerable.Range(0, rc).OrderBy(_ => rnd.Next()).Take(dropRecovery).ToHashSet();

        var dwork = Enumerable.Range(0, LeopardCodec.DecodeWorkCount(k, rc)).Select(_ => new byte[bytes]).ToArray();
        var decoding = new System.Collections.Concurrent.ConcurrentBag<double>();
        WithPointers(orig, op => WithPointers(recovery, rp => WithPointers(dwork, wp =>
        {
            var o = op.ToArray();
            var r = rp.ToArray();
            foreach (var i in lostOrig)
            {
                o[i] = 0;
            }

            foreach (var i in lostRec)
            {
                r[i] = 0;
            }

            LeopardCodec.Decode(field, o, r, wp, bytes, threads: 3, progress: decoding.Add);
        })));
        Assert.AreEqual(1.0, decoding.Max(), 1e-12, "decoding reports up to 100%");
        foreach (var i in lostOrig)
        {
            CollectionAssert.AreEqual(orig[i], dwork[i], $"original {i}");
        }
    }

    [TestMethod]
    public void Validation()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LeopardCodec.Validate(LeopardField.FF8, 2, 3));
        Assert.ThrowsExactly<ArgumentException>(() => LeopardCodec.Validate(LeopardField.FF8, 300, 10));
        Assert.AreEqual(LeopardField.FF16, LeopardCodec.SelectField(300, 10));
        Assert.AreEqual(1, LeopardCodec.NextPow2(1));
        Assert.AreEqual(64, LeopardCodec.NextPow2(33));
    }

    private static void WithPointers(byte[][] arrays, Action<nint[]> action)
    {
        var handles = arrays.Select(a => System.Runtime.InteropServices.GCHandle.Alloc(a, System.Runtime.InteropServices.GCHandleType.Pinned)).ToArray();
        try
        {
            action(handles.Select(h => h.AddrOfPinnedObject()).ToArray());
        }
        finally
        {
            foreach (var h in handles)
            {
                h.Free();
            }
        }
    }
}

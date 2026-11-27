using DeveParTools.Core.Hashing;

namespace DeveParTools.Core.Tests.Hashing;

[TestClass]
public sealed class Blake3Tests
{
    /// <summary>16 = AVX-512 when present, 8 = AVX2, 4 = SSE/NEON, 1 = scalar. Unsupported widths fall back to narrower ones.</summary>
    private static readonly int[] LaneWidths = [16, 8, 4, 1];

    private static IEnumerable<(int Length, byte[] Expected)> Vectors() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestData", "blake3_vectors.txt"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' '))
            .Select(p => (int.Parse(p[0]), Convert.FromHexString(p[1])));

    [TestCleanup]
    public void RestoreLanes() => Blake3Simd.MaxLanes = 16;

    [TestMethod]
    public void OfficialVectors_OneShot_AllLaneWidths_ExtendedOutput()
    {
        foreach (int lanes in LaneWidths)
        {
            Blake3Simd.MaxLanes = lanes;
            foreach (var (length, expected) in Vectors())
            {
                var input = MakeInput(length);
                var output = new byte[expected.Length];
                Blake3.Hash(input, output);
                CollectionAssert.AreEqual(expected, output, $"lanes={lanes} length={length}");
                CollectionAssert.AreEqual(expected.AsSpan(0, 32).ToArray(), Blake3.Hash(input), $"lanes={lanes} length={length} (32 bytes)");
            }
        }
    }

    [TestMethod]
    public void OfficialVectors_Streaming_AnyPieceSize()
    {
        foreach (int lanes in new[] { 8, 1 })
        {
            Blake3Simd.MaxLanes = lanes;
            foreach (var (length, expected) in Vectors())
            {
                var input = MakeInput(length);
                foreach (int piece in new[] { 1, 63, 64, 65, 1000, 1024, 1025, 2048, 4096, 5000, int.MaxValue })
                {
                    var hasher = new Blake3Hasher();
                    for (int pos = 0; pos < length; pos += piece)
                    {
                        hasher.Update(input.AsSpan(pos, Math.Min(piece, length - pos)));
                    }

                    var output = new byte[expected.Length];
                    hasher.Finalize(output);
                    CollectionAssert.AreEqual(expected, output, $"lanes={lanes} length={length} piece={piece}");
                }
            }
        }
    }

    [TestMethod]
    public void HasherCanBeResetAndFinalizedTwice()
    {
        var input = MakeInput(5000);
        var hasher = new Blake3Hasher();
        hasher.Update(MakeInput(777));
        hasher.Reset();
        hasher.Update(input);
        var first = hasher.Finalize();
        CollectionAssert.AreEqual(first, hasher.Finalize());
        CollectionAssert.AreEqual(Blake3.Hash(input), first);
    }

    /// <summary>
    /// Large inputs take the parallel subtree path and the 64 MiB subtree cap. Checked against a deliberately simple
    /// recursive implementation of the BLAKE3 tree rule (left subtree = largest power of two below n).
    /// </summary>
    [TestMethod]
    [DataRow(1 << 20)]
    [DataRow((1 << 20) + 1)]
    [DataRow((3 << 20) + 12345)]
    [DataRow((int)Blake3State.MaxSubtreeLen + 3 * 1024 + 5)]
    public void LargeInputs_MatchReferenceTree(int length)
    {
        var input = new byte[length];
        new Random(length).NextBytes(input);
        var expected = ReferenceHash(input);
        CollectionAssert.AreEqual(expected, Blake3.Hash(input), "one-shot");

        var hasher = new Blake3Hasher();
        int pos = 0;
        var rnd = new Random(1);
        while (pos < length)
        {
            // Odd sizes, some large: exercises unaligned subtrees, the chunk buffer and the parallel path.
            int piece = Math.Min(length - pos, rnd.Next(4) == 0 ? rnd.Next(1, 3 << 20) : rnd.Next(1, 70_000));
            hasher.Update(input.AsSpan(pos, piece));
            pos += piece;
        }

        CollectionAssert.AreEqual(expected, hasher.Finalize(), "streaming");
    }

    [TestMethod]
    public void AllLaneWidthsAgree_OnChunkAndParentBatches()
    {
        // 16 + 8 + 4 + 3 chunks: every width plus the scalar tail.
        var input = new byte[31 * 1024 + 17];
        new Random(5).NextBytes(input);
        Blake3Simd.MaxLanes = 1;
        var expected = Blake3.Hash(input);
        foreach (int lanes in LaneWidths)
        {
            Blake3Simd.MaxLanes = lanes;
            CollectionAssert.AreEqual(expected, Blake3.Hash(input), $"lanes={lanes}");
        }
    }

    private static byte[] ReferenceHash(byte[] input)
    {
        int chunks = Math.Max(1, (input.Length + Blake3.ChunkLen - 1) / Blake3.ChunkLen);
        var root = Node(input, 0, chunks);
        var digest = new byte[32];
        root.RootBytes(digest);
        return digest;

        static Blake3Output Node(byte[] input, int first, int count)
        {
            if (count == 1)
            {
                var chunk = new Blake3ChunkState((ulong)first);
                int start = first * Blake3.ChunkLen;
                chunk.Update(input.AsSpan(start, Math.Min(Blake3.ChunkLen, input.Length - start)));
                return chunk.Output();
            }

            int left = 1;
            while (left * 2 < count)
            {
                left *= 2;
            }

            var l = new uint[8];
            var r = new uint[8];
            Node(input, first, left).ChainingValue(l);
            Node(input, first + left, count - left).ChainingValue(r);
            return Blake3Output.Parent(l, r);
        }
    }

    private static byte[] MakeInput(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)(i % 251);
        }

        return b;
    }
}

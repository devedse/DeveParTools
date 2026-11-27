using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using DeveParTools.Core.Hashing;

namespace DeveParTools.Core.Tests.Hashing;

/// <summary>The internal building blocks of the BLAKE3 tree: <see cref="Blake3Tree"/>, <see cref="Blake3Simd"/> and the word helpers.</summary>
[TestClass]
public sealed unsafe class Blake3TreeTests
{
    private static readonly int[] LaneWidths = [16, 8, 4, 1];

    [TestCleanup]
    public void RestoreLanes() => Blake3Simd.MaxLanes = 16;

    /// <summary>
    /// <see cref="Blake3Tree.ReduceTo"/> merges level by level and carries an odd last node up unchanged. That must
    /// give the tree of BLAKE3's rule "left subtree = largest power of two below n" for every count, not only for the
    /// powers of two the hasher itself passes (see the 5-chunk picture in the remarks on <see cref="Blake3"/>).
    /// </summary>
    [TestMethod]
    public void ReduceTo_AnyCount_BuildsTheBlake3Tree()
    {
        var rnd = new Random(1);
        foreach (int lanes in LaneWidths)
        {
            Blake3Simd.MaxLanes = lanes;
            foreach (int count in new[] { 1, 2, 3, 5, 6, 7, 11, 17, 33 })
            {
                var leaves = new uint[count * 8];
                for (int i = 0; i < leaves.Length; i++)
                {
                    leaves[i] = (uint)rnd.Next();
                }

                var cvs = (uint[])leaves.Clone();
                Blake3Tree.ReduceTo(cvs, count, 1);
                CollectionAssert.AreEqual(ReferenceTree(leaves, 0, count), cvs[..8], $"lanes={lanes} count={count}");

                if (count >= 2)
                {
                    // Reducing to two gives the root's children: left = largest power of two below count.
                    cvs = (uint[])leaves.Clone();
                    Blake3Tree.ReduceTo(cvs, count, 2);
                    int left = LargestPowerOfTwoBelow(count);
                    CollectionAssert.AreEqual(ReferenceTree(leaves, 0, left), cvs[..8], $"lanes={lanes} count={count} left");
                    CollectionAssert.AreEqual(ReferenceTree(leaves, left, count - left), cvs[8..16], $"lanes={lanes} count={count} right");
                }
            }
        }
    }

    [TestMethod]
    public void HashMany_AllLaneWidthsAgree_ForChunksAndParents()
    {
        // 16 + 8 + 4 + 3 inputs: every width plus the scalar remainder; a non-zero start counter.
        const int count = 31;
        var chunks = new byte[count * Blake3.ChunkLen];
        new Random(2).NextBytes(chunks);
        uint[]? expectedChunks = null, expectedParents = null;
        foreach (int lanes in new[] { 1, 4, 8, 16 })
        {
            Blake3Simd.MaxLanes = lanes;
            var chunkCvs = new uint[count * 8];
            var parentCvs = new uint[count * 8];
            fixed (byte* p = chunks)
            fixed (uint* c = chunkCvs)
            fixed (uint* o = parentCvs)
            {
                Blake3Simd.HashMany(p, count, Blake3.ChunkLen, Blake3.BlocksPerChunk, 1000, true, 0, Blake3.ChunkStart, Blake3.ChunkEnd, c);
                Blake3Simd.HashMany(p, count, Blake3.BlockLen, 1, 0, false, Blake3.Parent, 0, 0, o);
            }

            expectedChunks ??= chunkCvs;
            expectedParents ??= parentCvs;
            CollectionAssert.AreEqual(expectedChunks, chunkCvs, $"chunks lanes={lanes}");
            CollectionAssert.AreEqual(expectedParents, parentCvs, $"parents lanes={lanes}");
        }

        // The scalar result for chunk 5 is the chunk state's chaining value (counter 1005).
        var state = new Blake3ChunkState(1005);
        state.Update(chunks.AsSpan(5 * Blake3.ChunkLen, Blake3.ChunkLen));
        var cv = new uint[8];
        state.Output().ChainingValue(cv);
        CollectionAssert.AreEqual(cv, expectedChunks![40..48]);
    }

    [TestMethod]
    public void Transpose_SwapsRowsAndColumns()
    {
        var r0 = Vector128.Create(0u, 1, 2, 3);
        var r1 = Vector128.Create(10u, 11, 12, 13);
        var r2 = Vector128.Create(20u, 21, 22, 23);
        var r3 = Vector128.Create(30u, 31, 32, 33);
        Lanes4.Transpose(ref r0, ref r1, ref r2, ref r3);
        Assert.AreEqual(Vector128.Create(0u, 10, 20, 30), r0);
        Assert.AreEqual(Vector128.Create(1u, 11, 21, 31), r1);
        Assert.AreEqual(Vector128.Create(2u, 12, 22, 32), r2);
        Assert.AreEqual(Vector128.Create(3u, 13, 23, 33), r3);
    }

    [TestMethod]
    public void Rotations_MatchScalarRotation()
    {
        var x = Vector128.Create(0x01234567u, 0x89ABCDEF, 0xDEADBEEF, 0x00000001);
        AssertLanes(16, Lanes4.Ror16(x), x);
        AssertLanes(12, Lanes4.Ror12(x), x);
        AssertLanes(8, Lanes4.Ror8(x), x);
        AssertLanes(7, Lanes4.Ror7(x), x);

        static void AssertLanes(int n, Vector128<uint> actual, Vector128<uint> x)
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(System.Numerics.BitOperations.RotateRight(x[i], n), actual[i], $"ror {n} lane {i}");
            }
        }
    }

    [TestMethod]
    public void LoadWords_ReadsLittleEndianWords()
    {
        var block = new byte[Blake3.BlockLen + 3];
        new Random(3).NextBytes(block);
        var words = new uint[16];
        Blake3Compression.LoadWords(block, words);
        for (int i = 0; i < 16; i++)
        {
            Assert.AreEqual(BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4)), words[i]);
        }
    }

    [TestMethod]
    public void CompressSubtreeToTwo_MatchesTheReferenceChildren()
    {
        // 2^11 chunks = 2 MiB: above the parallel threshold, so batches of 256 chunks are reduced on worker threads.
        const int chunks = 2048;
        var input = new byte[chunks * Blake3.ChunkLen];
        new Random(4).NextBytes(input);
        var leaves = new uint[chunks * 8];
        Blake3Tree.HashChunks(input, 4096, leaves);
        var two = new uint[16];
        Blake3Tree.CompressSubtreeToTwo(input, 4096, two);
        CollectionAssert.AreEqual(ReferenceTree(leaves, 0, chunks / 2), two[..8]);
        CollectionAssert.AreEqual(ReferenceTree(leaves, chunks / 2, chunks / 2), two[8..]);
    }

    /// <summary>The CV of the subtree over leaves [first, first + count), built recursively by the BLAKE3 rule.</summary>
    private static uint[] ReferenceTree(uint[] leaves, int first, int count)
    {
        if (count == 1)
        {
            return leaves[(first * 8)..(first * 8 + 8)];
        }

        int left = LargestPowerOfTwoBelow(count);
        var cv = new uint[8];
        Blake3Output.Parent(ReferenceTree(leaves, first, left), ReferenceTree(leaves, first + left, count - left)).ChainingValue(cv);
        return cv;
    }

    private static int LargestPowerOfTwoBelow(int n)
    {
        int p = 1;
        while (p * 2 < n)
        {
            p *= 2;
        }

        return p;
    }
}

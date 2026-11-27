using System.Buffers.Binary;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests.Packets;

/// <summary>Chunk descriptions of File packets (spec "File Packet", with its examples).</summary>
[TestClass]
public sealed class ChunkTests
{
    private const ulong Bs = 2000;

    private static byte[] Encode(ulong blockSize, params Par3Chunk[] chunks)
    {
        var list = new List<byte>();
        foreach (var c in chunks)
        {
            c.WriteTo(list, blockSize);
        }

        return list.ToArray();
    }

    private static ulong[] Words(byte[] bytes) => Enumerable.Range(0, bytes.Length / 8).Select(i => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8 * i))).ToArray();

    [TestMethod]
    public void SpecExample1_WholeBlocks()
    {
        // 10,000-byte file, block size 2,000: {length=10000, firstblockindex=0}.
        var bytes = Encode(Bs, new Par3Chunk { Length = 10_000, FirstBlock = 0 });
        CollectionAssert.AreEqual(new ulong[] { 10_000, 0 }, Words(bytes));
        var c = Par3Chunk.ParseAll(bytes, Bs).Single();
        Assert.AreEqual(5UL, c.FullBlocks(Bs));
        Assert.AreEqual(0UL, c.TailLength(Bs));
        Assert.IsNull(c.InlineTail);
    }

    [TestMethod]
    public void SpecExample3_ThreeChunksWithPackedTails()
    {
        // {5000, first 0, tail block 2 @ 0}, {5000, first 6, tail block 2 @ 1000}, {5000, first 3, tail block 5 @ 0}.
        var chunks = new[]
        {
            new Par3Chunk { Length = 5000, FirstBlock = 0, TailCrc = 1, TailHash = Fingerprint.Compute("a"u8), TailBlock = 2, TailOffset = 0 },
            new Par3Chunk { Length = 5000, FirstBlock = 6, TailCrc = 2, TailHash = Fingerprint.Compute("b"u8), TailBlock = 2, TailOffset = 1000 },
            new Par3Chunk { Length = 5000, FirstBlock = 3, TailCrc = 3, TailHash = Fingerprint.Compute("c"u8), TailBlock = 5, TailOffset = 0 },
        };
        var bytes = Encode(Bs, chunks);
        Assert.AreEqual(3 * (8 + 8 + 40), bytes.Length);
        CollectionAssert.AreEqual(chunks, Par3Chunk.ParseAll(bytes, Bs));
    }

    [TestMethod]
    public void SpecExample4_TailOnlyChunkHasNoFirstBlock()
    {
        // {length=1000, tailblockindex=1, tailoffset=1000}: shorter than a block, so no first block index.
        var chunk = new Par3Chunk { Length = 1000, TailCrc = 7, TailHash = Fingerprint.Compute("z"u8), TailBlock = 1, TailOffset = 1000 };
        var bytes = Encode(Bs, chunk);
        Assert.AreEqual(8 + 40, bytes.Length);
        var parsed = Par3Chunk.ParseAll(bytes, Bs).Single();
        Assert.AreEqual(chunk, parsed);
        Assert.AreEqual(0UL, parsed.FullBlocks(Bs));
        Assert.AreEqual(1000UL, parsed.TailLength(Bs));
    }

    [TestMethod]
    public void SpecExample5_TinyFileIsStoredInline()
    {
        // {length=10, taildata="qrstuvwxyz"}.
        var chunk = new Par3Chunk { Length = 10, InlineTail = "qrstuvwxyz"u8.ToArray() };
        var bytes = Encode(Bs, chunk);
        CollectionAssert.AreEqual((byte[])[10, 0, 0, 0, 0, 0, 0, 0, .. "qrstuvwxyz"u8], bytes);
        CollectionAssert.AreEqual("qrstuvwxyz"u8.ToArray(), Par3Chunk.ParseAll(bytes, Bs).Single().InlineTail);
    }

    [TestMethod]
    public void SpecExample6_UnprotectedMiddle()
    {
        // {2000, first 0}, {length=0, unprotectedlength=6000}, {2000, first 1}.
        var bytes = Encode(Bs, new Par3Chunk { Length = 2000, FirstBlock = 0 }, new Par3Chunk { Protected = false, Length = 6000 }, new Par3Chunk { Length = 2000, FirstBlock = 1 });
        CollectionAssert.AreEqual(new ulong[] { 2000, 0, 0, 6000, 2000, 1 }, Words(bytes));
        var parsed = Par3Chunk.ParseAll(bytes, Bs);
        Assert.IsFalse(parsed[1].Protected);
        Assert.AreEqual(6000UL, parsed[1].Length);
        Assert.AreEqual(0UL, parsed[1].FullBlocks(Bs));
        Assert.AreEqual(0UL, parsed[1].TailLength(Bs));
        Assert.AreEqual(1UL, parsed[2].FirstBlock);
    }

    [TestMethod]
    [DataRow(39, true)]
    [DataRow(40, false)]
    public void InlineLimit_TailsBelow40BytesAreInline(int tail, bool inline)
    {
        var chunk = inline
            ? new Par3Chunk { Length = Bs + (ulong)tail, FirstBlock = 3, InlineTail = new byte[tail] }
            : new Par3Chunk { Length = Bs + (ulong)tail, FirstBlock = 3, TailBlock = 4 };
        var bytes = Encode(Bs, chunk);
        Assert.AreEqual(16 + (inline ? tail : 40), bytes.Length);
        var parsed = Par3Chunk.ParseAll(bytes, Bs).Single();
        Assert.AreEqual(inline, parsed.InlineTail != null);
    }

    [TestMethod]
    public void EmptyDescription_IsAnEmptyFile() => Assert.AreEqual(0, Par3Chunk.ParseAll([], Bs).Count);

    [TestMethod]
    [DataRow(new byte[] { 1, 2, 3 }, DisplayName = "length cut")]
    [DataRow(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 }, DisplayName = "unprotected length cut")]
    [DataRow(new byte[] { 0xD0, 0x07, 0, 0, 0, 0, 0, 0, 1, 2 }, DisplayName = "first block cut")]
    [DataRow(new byte[] { 0xE8, 0x03, 0, 0, 0, 0, 0, 0, 1, 2, 3 }, DisplayName = "tail description cut")]
    [DataRow(new byte[] { 5, 0, 0, 0, 0, 0, 0, 0, 1, 2 }, DisplayName = "inline tail cut")]
    public void Truncated_Throws(byte[] bytes)
        => Assert.ThrowsExactly<InvalidDataException>(() => Par3Chunk.ParseAll(bytes, Bs));

    [TestMethod]
    public void MixedSequence_RoundTrips()
    {
        var chunks = new[]
        {
            new Par3Chunk { Protected = false, Length = 1 },
            new Par3Chunk { Length = 3 * Bs, FirstBlock = 10 },
            new Par3Chunk { Length = 25, InlineTail = TestData.Random(1, 25) },
            new Par3Chunk { Length = Bs + 500, FirstBlock = 1, TailCrc = 99, TailHash = Fingerprint.Compute("t"u8), TailBlock = 2, TailOffset = 1500 },
        };
        var parsed = Par3Chunk.ParseAll(Encode(Bs, chunks), Bs);
        Assert.AreEqual(chunks.Length, parsed.Count);
        Assert.AreEqual(chunks[0], parsed[0]);
        Assert.AreEqual(chunks[1], parsed[1]);
        CollectionAssert.AreEqual(chunks[2].InlineTail, parsed[2].InlineTail);
        Assert.AreEqual(chunks[3], parsed[3]);
    }
}

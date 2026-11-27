using System.Buffers.Binary;
using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests.Packets;

/// <summary>Reading damaged, truncated and unusual .par2 files: every intact packet must be found.</summary>
[TestClass]
public sealed class Par2PacketReaderTests
{
    private static readonly Md5Hash SetId = Md5Hash.Compute("set"u8);

    private static byte[] Creator(string text) => Par2PacketWriter.Creator(SetId, text);

    private static byte[] Recovery(uint exponent, int dataLength, int seed = 1)
        => PacketSurgery.Reseal([.. Par2PacketWriter.RecoverySliceHeader(SetId, exponent, dataLength), .. TestData.Random(dataLength, seed)]);

    private static Par2FileReadResult Read(TempDir dir, params byte[][] parts) => Par2PacketReader.ReadFile(dir.Write("t.par2", parts.SelectMany(p => p).ToArray()));

    private static string[] Texts(Par2FileReadResult r) => r.Packets.OfType<Par2CreatorPacket>().Select(c => c.Text).ToArray();

    [TestMethod]
    public void EmptyShortAndGarbageFiles_HaveNoPackets()
    {
        using var dir = new TempDir();
        foreach (var content in new[] { Array.Empty<byte>(), new byte[63], TestData.Random(5000, 1), [.. Par2PacketTypes.Magic, .. new byte[40]] })
        {
            var r = Read(dir, content);
            Assert.AreEqual(0, r.Packets.Count);
            Assert.AreEqual(0, r.DamagedPackets);
        }

        // A header-sized magic with length 0 is a damaged packet.
        var lone = Read(dir, Par2PacketTypes.Magic, new byte[56]);
        Assert.AreEqual(0, lone.Packets.Count);
        Assert.AreEqual(1, lone.DamagedPackets);
    }

    [TestMethod]
    public void GarbageAroundPackets_IsSkipped()
    {
        using var dir = new TempDir();
        var r = Read(dir, TestData.Random(13, 1), Creator("one"), TestData.Random(7, 2), Creator("two"), TestData.Random(100, 3));
        CollectionAssert.AreEqual(new[] { "one", "two" }, Texts(r));
        Assert.AreEqual(0, r.DamagedPackets);
        Assert.AreEqual(dir["t.par2"], r.Path);
    }

    [TestMethod]
    public void DamagedPacket_IsCounted_AndTheNextOneIsFound()
    {
        using var dir = new TempDir();
        var bad = Creator("damaged");
        bad[70] ^= 1;
        var r = Read(dir, Creator("one"), bad, Creator("two"));
        CollectionAssert.AreEqual(new[] { "one", "two" }, Texts(r));
        Assert.AreEqual(1, r.DamagedPackets);
    }

    [TestMethod]
    [DataRow(0UL, DisplayName = "length 0")]
    [DataRow(60UL, DisplayName = "length smaller than the header")]
    [DataRow(66UL, DisplayName = "length not a multiple of 4")]
    [DataRow(1UL << 40, DisplayName = "length beyond the end of the file")]
    [DataRow(0x7FFF_FFFF_FFFF_FFFCUL, DisplayName = "length that overflows offset + length")]
    [DataRow(0xFFFF_FFFF_FFFF_FFFCUL, DisplayName = "length of 2^64 - 4 (negative as long)")]
    public void ImpossibleLength_IsADamagedPacket(ulong length)
    {
        using var dir = new TempDir();
        foreach (var bad in new[] { Creator("bad"), Recovery(5, 8) })
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bad.AsSpan(8), length);
            // Placed after another packet (offset > 0): there "offset + length" used to overflow and pass the check.
            var r = Read(dir, Creator("one"), bad, Creator("two"));
            CollectionAssert.AreEqual(new[] { "one", "two" }, Texts(r));
            Assert.AreEqual(1, r.DamagedPackets);
        }
    }

    [TestMethod]
    public void TruncatedLastPacket_IsADamagedPacket()
    {
        using var dir = new TempDir();
        var last = Recovery(1, 4000);
        var r = Read(dir, Creator("one"), last.AsSpan(0, 2000).ToArray());
        CollectionAssert.AreEqual(new[] { "one" }, Texts(r));
        Assert.AreEqual(1, r.DamagedPackets);
    }

    [TestMethod]
    public void UnknownPacketType_IsSkippedByItsLength_AndNotCounted()
    {
        using var dir = new TempDir();

        // The unknown packet contains a fake (damaged) packet; jumping over it must not even look at that.
        var inner = Creator("inside");
        inner[70] ^= 1;
        var unknown = Par2PacketWriter.Build(SetId, "PAR 2.0\0NewThing"u8, inner);
        var r = Read(dir, Creator("one"), unknown, Creator("two"));
        CollectionAssert.AreEqual(new[] { "one", "two" }, Texts(r));
        Assert.AreEqual(0, r.DamagedPackets);
    }

    [TestMethod]
    public void MalformedPacketWithValidMd5_IsCountedAndSkippedByItsLength()
    {
        using var dir = new TempDir();
        var shortMain = Par2PacketWriter.Build(SetId, Par2PacketTypes.Main, new byte[8]);
        var emptyRecovery = Par2PacketWriter.Build(SetId, Par2PacketTypes.RecoverySlice, []);
        var r = Read(dir, Creator("one"), shortMain, emptyRecovery, Creator("two"));
        CollectionAssert.AreEqual(new[] { "one", "two" }, Texts(r));
        Assert.AreEqual(2, r.DamagedPackets);
    }

    [TestMethod]
    public void LargeRecoveryPacket_IsHashedInChunks_AndItsDataStaysOnDisk()
    {
        using var dir = new TempDir();
        int size = (3 << 20) + 12;
        var r = Read(dir, Creator("one"), Recovery(9, size), Creator("two"));
        var rec = r.Packets.OfType<Par2RecoverySlicePacket>().Single();
        Assert.AreEqual(9u, rec.Exponent);
        Assert.AreEqual(size, rec.DataLength);
        CollectionAssert.AreEqual(new[] { "one", "two" }, Texts(r));

        // One flipped bit in the last chunk is detected.
        var bytes = File.ReadAllBytes(dir["t.par2"]);
        bytes[rec.DataOffset + size - 1] ^= 1;
        File.WriteAllBytes(dir["t.par2"], bytes);
        r = Par2PacketReader.ReadFile(dir["t.par2"]);
        Assert.AreEqual(0, r.Packets.OfType<Par2RecoverySlicePacket>().Count());
        Assert.AreEqual(1, r.DamagedPackets);
        Assert.AreEqual(2, Texts(r).Length);
    }

    [TestMethod]
    public void HugeNonRecoveryPacket_IsRejectedWithoutReadingIt()
    {
        using var dir = new TempDir();
        long length = (64L << 20) + 64 + 4;
        var path = dir["huge.par2"];
        using (var fs = new FileStream(path, FileMode.Create))
        {
            // A sparse file: the claimed body is never read (it would have to be held in memory).
            var header = Creator("x").AsSpan(0, 64).ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)length);
            fs.Write(header);
            fs.SetLength(length);
            fs.Position = length;
            fs.Write(Creator("after"));
        }

        var r = Par2PacketReader.ReadFile(path);
        CollectionAssert.AreEqual(new[] { "after" }, Texts(r));
        Assert.AreEqual(1, r.DamagedPackets);
    }

    [TestMethod]
    [DataRow(-1, DisplayName = "magic ending with the first chunk")]
    [DataRow(3, DisplayName = "magic across the chunk boundary (found thanks to the 7-byte overlap)")]
    [DataRow(5000, DisplayName = "magic in the second chunk")]
    public void MagicSearch_FindsPacketsBeyondOneMegabyteOfGarbage(int shift)
    {
        using var dir = new TempDir();

        // The search starts at offset 1 and reads chunks of 1 MiB + 7 bytes that overlap by 7 bytes: chunk 1 is
        // [1, 1 MiB + 8), chunk 2 starts at 1 MiB + 1. The magic is placed at 1 + 1 MiB + shift.
        var garbage = TestData.Random(1 + (1 << 20) + shift, 4);
        Array.Clear(garbage, 0, 8);
        var r = Read(dir, garbage, Creator("found"));
        CollectionAssert.AreEqual(new[] { "found" }, Texts(r));
    }

    [TestMethod]
    public void MagicNearTheEnd_WithoutRoomForAHeader_EndsTheScan()
    {
        using var dir = new TempDir();
        var r = Read(dir, Creator("one"), TestData.Random(30, 5), Par2PacketTypes.Magic, new byte[20]);
        CollectionAssert.AreEqual(new[] { "one" }, Texts(r));
        Assert.AreEqual(0, r.DamagedPackets);
    }

    [TestMethod]
    public void Cancellation_IsObserved()
    {
        using var dir = new TempDir();
        var path = dir.Write("c.par2", Creator("one"));
        Assert.ThrowsExactly<OperationCanceledException>(() => Par2PacketReader.ReadFile(path, new CancellationToken(canceled: true)));
    }
}

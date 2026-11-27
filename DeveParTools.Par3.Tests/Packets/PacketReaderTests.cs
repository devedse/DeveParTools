using System.Buffers.Binary;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests.Packets;

/// <summary>Reading damaged, truncated, malformed and unknown packets, and finding packets inside other data.</summary>
[TestClass]
public sealed class PacketReaderTests
{
    private static readonly InputSetId Set = new(42);

    private static Par3FileReadResult Read(params byte[][] parts)
    {
        using var dir = new TempDir();
        var path = dir.Write("x.par3", parts.SelectMany(p => p).ToArray());
        return Par3PacketReader.ReadFile(path);
    }

    private static byte[] Comment(string text) => Par3PacketWriter.Text(Set, Par3PacketTypes.Comment, text);

    private static byte[] Packet(ulong type, int bodyLength) => Par3PacketWriter.Build(Set, type, new byte[bodyLength]);

    [TestMethod]
    public void EmptyFile_And_FileWithoutPackets()
    {
        Assert.AreEqual(0, Read().Packets.Count);
        var garbage = Read(TestData.Random(1, 5000));
        Assert.AreEqual(0, garbage.Packets.Count);
        Assert.AreEqual(0, garbage.DamagedPackets);
    }

    [TestMethod]
    public void PacketsBetweenGarbageAreFound()
    {
        var r = Read(TestData.Random(1, 100), Comment("a"), TestData.Random(2, 3), Comment("b"), TestData.Random(3, 60));
        CollectionAssert.AreEqual(new[] { "a", "b" }, r.Packets.Cast<Par3CommentPacket>().Select(c => c.Text).ToArray());
        Assert.AreEqual(0, r.DamagedPackets);
    }

    [TestMethod]
    public void ChecksumMismatch_CountsAsDamagedAndTheNextPacketIsStillRead()
    {
        var bad = Comment("damaged");
        bad[^1] ^= 1;
        var r = Read(bad, Comment("ok"));
        Assert.AreEqual("ok", ((Par3CommentPacket)r.Packets.Single()).Text);
        Assert.AreEqual(1, r.DamagedPackets);
    }

    [TestMethod]
    public void DamagedPacketContainingAnotherPacket_TheInnerOneIsFound()
    {
        // A damaged outer packet is not trusted, so the search continues inside it.
        var inner = Comment("inner");
        var outer = Par3PacketWriter.Build(Set, Par3PacketTypes.Creator, [.. "x"u8, .. inner]);
        outer[8] ^= 1;
        var r = Read(outer);
        Assert.AreEqual("inner", ((Par3CommentPacket)r.Packets.Single()).Text);
    }

    [TestMethod]
    public void IntactPacketContainingAnotherPacket_TheBodyIsSkipped()
    {
        // An intact packet's length is trusted: a .par3 file stored in a Data packet does not leak its packets.
        var inner = Comment("inner");
        var r = Read(Par3PacketWriter.Build(Set, Par3PacketTypes.Creator, [.. "x"u8, .. inner]));
        Assert.IsInstanceOfType<Par3CreatorPacket>(r.Packets.Single());
    }

    [TestMethod]
    public void UnknownPacketType_IsIgnoredNotDamaged()
    {
        // Application-specific packets (spec appendix B) must be ignored; their body is skipped.
        var app = Par3PacketWriter.Build(Set, Par3PacketTypes.FromAscii("ACME XYZ"), [.. Comment("hidden"), 1, 2, 3]);
        var r = Read(app, Comment("after"));
        Assert.AreEqual("after", ((Par3CommentPacket)r.Packets.Single()).Text);
        Assert.AreEqual(0, r.DamagedPackets);
    }

    [TestMethod]
    [DataRow(0UL)]
    [DataRow(47UL)]
    [DataRow(1_000_000UL)]
    [DataRow(ulong.MaxValue)]
    public void ImpossibleLengthField_IsDamaged(ulong length)
    {
        var p = Comment("x");
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(24), length);
        var r = Read(p, Comment("y"));
        Assert.AreEqual("y", ((Par3CommentPacket)r.Packets.Single()).Text);
        Assert.AreEqual(1, r.DamagedPackets);
    }

    [TestMethod]
    public void TruncatedPacketAtTheEnd_IsDamaged()
    {
        var p = Comment("truncated");
        var r = Read(Comment("first"), p[..^3]);
        Assert.AreEqual(1, r.Packets.Count);
        Assert.AreEqual(1, r.DamagedPackets);
    }

    [TestMethod]
    public void MagicSplitAcrossTheSearchWindow_IsFound()
    {
        // The search reads 1 MiB + 7 byte windows that overlap by 7 bytes. Put a packet so its magic starts 3 bytes
        // before the end of the first window: it is only complete in the second window.
        var junk = new byte[(1 << 20) + 7 - 3 + 1];
        junk[0] = (byte)'P'; // the file starts with something that is not a packet
        var r = Read(junk, Comment("far away"));
        Assert.AreEqual("far away", ((Par3CommentPacket)r.Packets.Single()).Text);
    }

    [TestMethod]
    public void LargeRecoveryPacket_IsHashedInPieces()
    {
        var data = TestData.Random(5, (1 << 20) + 1000);
        var header = Par3PacketWriter.RecoveryDataHeader(Set, default, default, 3, data.Length);
        var packet = (byte[])[.. header, .. data];
        Core.Hashing.Blake3.Hash(packet.AsSpan(24)).AsSpan(0, 16).CopyTo(packet.AsSpan(8));
        var r = Read(packet);
        var rec = (Par3RecoveryDataPacket)r.Packets.Single();
        Assert.AreEqual(3UL, rec.RecoveryIndex);
        Assert.AreEqual(data.Length, rec.DataLength);

        packet[^1] ^= 1;
        Assert.AreEqual(1, Read(packet).DamagedPackets);
    }

    [TestMethod]
    [DataRow(Par3PacketTypes.Data, 7)]
    [DataRow(Par3PacketTypes.RecoveryData, 39)]
    public void BulkPacketShorterThanItsFixedFields_IsDamaged(ulong type, int body)
    {
        var r = Read(Packet(type, body));
        Assert.AreEqual(0, r.Packets.Count);
        Assert.AreEqual(1, r.DamagedPackets);
    }

    public static IEnumerable<object[]> MalformedBodies()
    {
        // Each body has a valid checksum but does not fit the layout of its type.
        yield return ["start too short", Par3PacketTypes.Start, new byte[20]];
        yield return ["start gf size mismatch", Par3PacketTypes.Start, StartWithGf(41, 9)];
        yield return ["start block size 0", Par3PacketTypes.Start, Par3PacketWriter.StartBody(default, default, 0, [0x1D])];
        yield return ["ext too short", Par3PacketTypes.ExternalData, new byte[7]];
        yield return ["ext partial tuple", Par3PacketTypes.ExternalData, new byte[8 + 23]];
        yield return ["cauchy", Par3PacketTypes.CauchyMatrix, new byte[23]];
        yield return ["sparse", Par3PacketTypes.SparseMatrix, new byte[39]];
        yield return ["fft", Par3PacketTypes.FftMatrix, new byte[16]];
        yield return ["erd too short", Par3PacketTypes.RecoveryExternalData, new byte[39]];
        yield return ["erd partial tuple", Par3PacketTypes.RecoveryExternalData, new byte[40 + 1]];
        yield return ["file empty", Par3PacketTypes.File, new byte[1]];
        yield return ["file name longer than body", Par3PacketTypes.File, (byte[])[10, 0, 1, 2]];
        yield return ["file options missing", Par3PacketTypes.File, (byte[])[0, 0, .. new byte[24], 3]];
        yield return ["dir empty", Par3PacketTypes.Directory, new byte[1]];
        yield return ["dir name longer than body", Par3PacketTypes.Directory, (byte[])[5, 0, 1]];
        yield return ["dir options missing", Par3PacketTypes.Directory, (byte[])[0, 0, 1, 0, 0, 0]];
        yield return ["dir option count wraps 32-bit", Par3PacketTypes.Directory, (byte[])[0, 0, 0, 0, 0, 0x10, .. new byte[16]]];
        yield return ["dir partial child", Par3PacketTypes.Directory, (byte[])[0, 0, 0, 0, 0, 0, 1, 2, 3]];
        yield return ["root too short", Par3PacketTypes.Root, new byte[12]];
        yield return ["root option count wraps 32-bit", Par3PacketTypes.Root, (byte[])[.. new byte[9], 0, 0, 0, 0x10, .. new byte[16]]];
        yield return ["root partial child", Par3PacketTypes.Root, new byte[13 + 5]];
        yield return ["link too short", Par3PacketTypes.Link, new byte[2]];
        yield return ["link name longer than body", Par3PacketTypes.Link, (byte[])[1, 9, 0]];
        yield return ["link path longer than body", Par3PacketTypes.Link, (byte[])[1, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0]];
        yield return ["link no option count", Par3PacketTypes.Link, (byte[])[1, 0, 0, 1, 0, 0, 0, (byte)'a']];
        yield return ["link options missing", Par3PacketTypes.Link, (byte[])[1, 0, 0, 1, 0, 0, 0, (byte)'a', 2]];
        yield return ["unix too short", Par3PacketTypes.UnixPermissions, new byte[34]];
        yield return ["unix owner longer than body", Par3PacketTypes.UnixPermissions, (byte[])[.. new byte[34], 5]];
        yield return ["unix group longer than body", Par3PacketTypes.UnixPermissions, (byte[])[.. new byte[34], 0, 5]];
        yield return ["unix xattr name length cut", Par3PacketTypes.UnixPermissions, (byte[])[.. new byte[34], 0, 0, 1]];
        yield return ["unix xattr name longer than body", Par3PacketTypes.UnixPermissions, (byte[])[.. new byte[34], 0, 0, 9, 0, 1]];
        yield return ["unix xattr value longer than body", Par3PacketTypes.UnixPermissions, (byte[])[.. new byte[34], 0, 0, 1, 0, (byte)'n', 9, 0]];
        yield return ["fat", Par3PacketTypes.FatPermissions, new byte[25]];
    }

    private static byte[] StartWithGf(int length, byte gf)
    {
        var b = new byte[length];
        b[40] = gf;
        return b;
    }

    [TestMethod]
    [DynamicData(nameof(MalformedBodies))]
    public void MalformedBody_IsDamagedAndSkipped(string name, ulong type, byte[] body)
    {
        // The packet's checksum is fine, so its length is trusted and the next packet is read normally.
        var r = Read(Par3PacketWriter.Build(Set, type, body), Comment("next"));
        Assert.AreEqual("next", ((Par3CommentPacket)r.Packets.Single()).Text, name);
        Assert.AreEqual(1, r.DamagedPackets, name);
    }

    [TestMethod]
    public void TooLargeMetadataPacket_IsDamaged()
    {
        // Metadata packets are read into memory, so a length over 256 MiB is refused without reading the body.
        var p = Comment("x");
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(24), (256UL << 20) + 49);
        using var dir = new TempDir();
        var path = dir.Write("big.par3", p);
        using (var fs = new FileStream(path, FileMode.Open))
        {
            fs.SetLength((256L << 20) + 49 + 100); // sparse file
        }

        var r = Par3PacketReader.ReadFile(path);
        Assert.AreEqual(0, r.Packets.Count);
        Assert.AreEqual(1, r.DamagedPackets);
    }

    [TestMethod]
    public void Cancellation()
    {
        using var dir = new TempDir();
        var path = dir.Write("c.par3", Comment("x"));
        Assert.ThrowsExactly<OperationCanceledException>(() => Par3PacketReader.ReadFile(path, new CancellationToken(true)));
    }
}

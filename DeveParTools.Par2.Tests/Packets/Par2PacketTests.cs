using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests.Packets;

/// <summary>Packet layout (PAR 2.0 specification) and the writer → reader round trip of every packet type.</summary>
[TestClass]
public sealed class Par2PacketTests
{
    private static readonly Md5Hash SetId = Md5Hash.Compute("set"u8);

    private static Par2Packet ReadSingle(TempDir dir, byte[] packet)
    {
        var r = Par2PacketReader.ReadFile(dir.Write("p.par2", packet));
        Assert.AreEqual(0, r.DamagedPackets);
        return r.Packets.Single();
    }

    [TestMethod]
    public void Md5Hash_BytesHexAndOrder()
    {
        var bytes = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var h = Md5Hash.FromBytes(bytes);
        CollectionAssert.AreEqual(bytes, h.ToArray());
        Assert.AreEqual("0102030405060708090a0b0c0d0e0f10", h.ToString());
        CollectionAssert.AreEqual(MD5.HashData("abc"u8), Md5Hash.Compute("abc"u8).ToArray());

        // Sorted "as 16-byte unsigned integers", compared from byte 15 down (par2cmdline): byte 15 dominates.
        var high = new byte[16];
        high[15] = 1;
        var low = new byte[16];
        low[0] = 0xFF;
        low[14] = 0xFF;
        Assert.IsTrue(Md5Hash.FromBytes(high).CompareTo(Md5Hash.FromBytes(low)) > 0);
        Assert.AreEqual(0, h.CompareTo(Md5Hash.FromBytes(bytes)));
    }

    [TestMethod]
    public void Build_WritesHeaderPadsBodyAndSealsWithMd5()
    {
        var p = Par2PacketWriter.Build(SetId, Par2PacketTypes.Creator, "abcde"u8);
        Assert.AreEqual(64 + 8, p.Length, "5 body bytes are padded to 8");
        CollectionAssert.AreEqual(Par2PacketTypes.Magic, p[..8]);
        Assert.AreEqual((ulong)p.Length, BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(8)));
        CollectionAssert.AreEqual(MD5.HashData(p.AsSpan(32)), p[16..32], "MD5 over set ID, type and body");
        CollectionAssert.AreEqual(SetId.ToArray(), p[32..48]);
        CollectionAssert.AreEqual(Par2PacketTypes.Creator, p[48..64]);
        CollectionAssert.AreEqual("abcde\0\0\0"u8.ToArray(), p[64..]);
    }

    [TestMethod]
    public void ComputeFileId_IsMd5Of16kHashLengthAndName()
    {
        var hash16k = Md5Hash.Compute("first 16k"u8);
        var expected = MD5.HashData([.. hash16k.ToArray(), .. BitConverter.GetBytes(1234L), .. "dir/ä.bin"u8.ToArray()]);
        CollectionAssert.AreEqual(expected, Par2PacketWriter.ComputeFileId(hash16k, 1234, "dir/ä.bin").ToArray());
    }

    [TestMethod]
    public void MainPacket_RoundTrip_WithRecoveryAndNonRecoveryFiles()
    {
        using var dir = new TempDir();
        var rec = new[] { Md5Hash.Compute("a"u8), Md5Hash.Compute("b"u8) };
        var non = new[] { Md5Hash.Compute("c"u8) };
        var main = (Par2MainPacket)ReadSingle(dir, Par2PacketWriter.Main(SetId, 4096, rec, non));
        Assert.AreEqual(SetId, main.SetId);
        Assert.AreEqual(4096, main.SliceSize);
        CollectionAssert.AreEqual(rec, main.RecoveryFileIds.ToArray());
        CollectionAssert.AreEqual(non, main.NonRecoveryFileIds.ToArray());
        Assert.AreEqual(12 + 3 * 16, Par2PacketWriter.MainBody(4096, rec, non).Length);
    }

    [TestMethod]
    public void FileDescription_RoundTrip_Utf8NameWithoutPadding()
    {
        using var dir = new TempDir();
        var id = Md5Hash.Compute("id"u8);
        var full = Md5Hash.Compute("full"u8);
        var k16 = Md5Hash.Compute("16k"u8);
        var d = (Par2FileDescriptionPacket)ReadSingle(dir, Par2PacketWriter.FileDescription(SetId, id, full, k16, 1L << 40, "sub/naïve.bin"));
        Assert.AreEqual(new Par2FileDescriptionPacket(SetId, id, full, k16, 1L << 40, "sub/naïve.bin"), d);
    }

    [TestMethod]
    public void SliceChecksum_RoundTrip()
    {
        using var dir = new TempDir();
        var id = Md5Hash.Compute("id"u8);
        var slices = new List<(Md5Hash, uint)> { (Md5Hash.Compute("s0"u8), 0xDEADBEEF), (Md5Hash.Compute("s1"u8), 7) };
        var c = (Par2SliceChecksumPacket)ReadSingle(dir, Par2PacketWriter.SliceChecksum(SetId, id, slices));
        Assert.AreEqual(id, c.FileId);
        CollectionAssert.AreEqual(slices, c.Slices.ToList());
    }

    [TestMethod]
    public void CreatorAndComments_RoundTrip()
    {
        using var dir = new TempDir();
        Assert.AreEqual(new Par2CreatorPacket(SetId, "tool 1.0"), ReadSingle(dir, Par2PacketWriter.Creator(SetId, "tool 1.0")));
        Assert.AreEqual(new Par2CommentPacket(SetId, "héllo", false), ReadSingle(dir, Par2PacketWriter.AsciiComment(SetId, "héllo")));

        // Unicode comment: 16 bytes (MD5 of the matching ASCII comment packet), then UTF-16LE text.
        var body = new byte[16].Concat(Encoding.Unicode.GetBytes("ünï")).ToArray();
        Assert.AreEqual(new Par2CommentPacket(SetId, "ünï", true), ReadSingle(dir, Par2PacketWriter.Build(SetId, Par2PacketTypes.UnicodeComment, body)));
    }

    [TestMethod]
    public void UnicodeFilename_RoundTrip()
    {
        using var dir = new TempDir();
        var id = Md5Hash.Compute("id"u8);
        var u = (Par2UnicodeFilenamePacket)ReadSingle(dir, Par2PacketWriter.UnicodeFilename(SetId, id, "日本/ファイル.bin"));
        Assert.AreEqual(new Par2UnicodeFilenamePacket(SetId, id, "日本/ファイル.bin"), u);
    }

    [TestMethod]
    public void RecoverySlice_HeaderPlusData_PointsIntoTheFile()
    {
        using var dir = new TempDir();
        var data = TestData.Random(64, 1);
        var packet = Par2PacketWriter.RecoverySliceHeader(SetId, 77, data.Length).Concat(data).ToArray();
        Assert.AreEqual((ulong)packet.Length, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(8)));
        PacketSurgery.Reseal(packet);

        var r = Par2PacketReader.ReadFile(dir.Write("r.par2", [.. new byte[12], .. packet]));
        var slice = (Par2RecoverySlicePacket)r.Packets.Single();
        Assert.AreEqual(new Par2RecoverySlicePacket(SetId, 77, dir["r.par2"], 12 + 64 + 4, 64), slice);
        CollectionAssert.AreEqual(data, File.ReadAllBytes(dir["r.par2"]).AsSpan((int)slice.DataOffset, (int)slice.DataLength).ToArray());
    }

    [TestMethod]
    [DataRow(nameof(Par2PacketTypes.Main), 8, DisplayName = "main shorter than its fixed part")]
    [DataRow(nameof(Par2PacketTypes.FileDescription), 52, DisplayName = "file description shorter than 56 bytes")]
    [DataRow(nameof(Par2PacketTypes.SliceChecksum), 12, DisplayName = "slice checksums without a file ID")]
    [DataRow(nameof(Par2PacketTypes.SliceChecksum), 16 + 24, DisplayName = "slice checksums with a partial entry")]
    [DataRow(nameof(Par2PacketTypes.UnicodeFilename), 12, DisplayName = "unicode file name without a file ID")]
    [DataRow(nameof(Par2PacketTypes.UnicodeComment), 12, DisplayName = "unicode comment without its MD5")]
    public void Parse_BodyTooShortForItsType_ReturnsNull(string typeName, int bodyLength)
    {
        var type = (byte[])typeof(Par2PacketTypes).GetField(typeName)!.GetValue(null)!;
        Assert.IsNull(Par2PacketReader.Parse(SetId, type, new byte[bodyLength]));
    }

    [TestMethod]
    [DataRow(0L, 0u, DisplayName = "slice size 0")]
    [DataRow(6L, 0u, DisplayName = "slice size not a multiple of 4")]
    [DataRow(-4L, 0u, DisplayName = "slice size of 2^64 - 4 (negative)")]
    [DataRow(4096L, 3u, DisplayName = "more recovery files than IDs")]
    public void Parse_InvalidMainPacket_ReturnsNull(long sliceSize, uint recoveryCount)
    {
        var body = Par2PacketWriter.MainBody(sliceSize, [Md5Hash.Compute("a"u8), Md5Hash.Compute("b"u8)], []);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), recoveryCount == 0 ? 2 : recoveryCount);
        Assert.IsNull(Par2PacketReader.Parse(SetId, Par2PacketTypes.Main, body));
    }

    [TestMethod]
    public void Parse_UnknownType_ReturnsNull()
        => Assert.IsNull(Par2PacketReader.Parse(SetId, "PAR 2.0\0Future\0\0"u8, new byte[32]));

    [TestMethod]
    public void Parse_MainPacketIgnoresATrailingPartialId()
    {
        var body = Par2PacketWriter.MainBody(4, [Md5Hash.Compute("a"u8)], []).Concat(new byte[8]).ToArray();
        var main = (Par2MainPacket)Par2PacketReader.Parse(SetId, Par2PacketTypes.Main, body)!;
        Assert.AreEqual(1, main.RecoveryFileIds.Count);
        Assert.AreEqual(0, main.NonRecoveryFileIds.Count);
    }

    [TestMethod]
    [DataRow(new byte[] { 0x61, 0, 0x62, 0, 0, 0, 0, 0 }, "ab", DisplayName = "zero padding is removed")]
    [DataRow(new byte[] { 0x61, 0, 0x62, 0, 0 }, "ab", DisplayName = "an odd trailing byte is dropped")]
    [DataRow(new byte[] { 0, 0x61, 0, 0 }, "愀", DisplayName = "a zero low byte inside a character stays")]
    [DataRow(new byte[] { 0, 0 }, "", DisplayName = "only padding")]
    public void DecodeName_Unicode(byte[] bytes, string expected)
        => Assert.AreEqual(expected, Par2PacketReader.DecodeName(bytes, unicode: true));

    [TestMethod]
    public void DecodeName_Ascii_IsUtf8WithoutPadding()
    {
        Assert.AreEqual("é.bin", Par2PacketReader.DecodeName([.. Encoding.UTF8.GetBytes("é.bin"), 0, 0, 0], unicode: false));
        Assert.AreEqual(string.Empty, Par2PacketReader.DecodeName([0, 0, 0, 0], unicode: false));
        Assert.AreEqual(string.Empty, Par2PacketReader.DecodeName([], unicode: false));
    }
}

using System.Buffers.Binary;
using DeveParTools.Core.Hashing;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests.Packets;

/// <summary>Every packet type: written with <see cref="Par3PacketWriter"/>, read back with <see cref="Par3PacketReader"/>.</summary>
[TestClass]
public sealed class PacketRoundTripTests
{
    private static readonly InputSetId Set = new(0x1122334455667788);

    private static Fingerprint Fp(int seed) => Fingerprint.FromBytes(TestData.Random(seed, 16));

    private static T RoundTrip<T>(byte[] packet) where T : Par3Packet
    {
        using var dir = new TempDir();
        var path = dir.Write("p.par3", [.. TestData.Random(9, 13), .. packet]);
        var read = Par3PacketReader.ReadFile(path);
        Assert.AreEqual(0, read.DamagedPackets);
        var p = (T)read.Packets.Single();
        Assert.AreEqual(Set, p.SetId);
        Assert.AreEqual(Par3PacketWriter.ChecksumOf(packet), p.Checksum);
        Assert.AreEqual(path, p.SourceFile);
        Assert.AreEqual(13, p.SourceOffset);
        return p;
    }

    [TestMethod]
    public void Header_LayoutAndChecksumFollowTheSpecification()
    {
        var packet = Par3PacketWriter.Build(Set, Par3PacketTypes.Comment, "hi"u8);
        Assert.AreEqual(50, packet.Length);
        CollectionAssert.AreEqual("PAR3\0PKT"u8.ToArray(), packet[..8]);
        Assert.AreEqual(50UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(24)));
        Assert.AreEqual(Set.Value, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(32)));
        Assert.AreEqual("PAR COM", Par3PacketTypes.ToAscii(BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(40))));

        // The checksum covers everything after itself: length, set id, type and body.
        CollectionAssert.AreEqual(Blake3.Hash(packet.AsSpan(24))[..16], packet[8..24]);
    }

    [TestMethod]
    [DataRow("PAR CRE", Par3PacketTypes.Creator)]
    [DataRow("PAR COM", Par3PacketTypes.Comment)]
    [DataRow("PAR STA", Par3PacketTypes.Start)]
    [DataRow("PAR DAT", Par3PacketTypes.Data)]
    [DataRow("PAR EXT", Par3PacketTypes.ExternalData)]
    [DataRow("PAR CAU", Par3PacketTypes.CauchyMatrix)]
    [DataRow("PAR SPA", Par3PacketTypes.SparseMatrix)]
    [DataRow("PAR EXP", Par3PacketTypes.ExplicitMatrix)]
    [DataRow("PAR FFT", Par3PacketTypes.FftMatrix)]
    [DataRow("PAR REC", Par3PacketTypes.RecoveryData)]
    [DataRow("PAR ERD", Par3PacketTypes.RecoveryExternalData)]
    [DataRow("PAR FIL", Par3PacketTypes.File)]
    [DataRow("PAR DIR", Par3PacketTypes.Directory)]
    [DataRow("PAR ROO", Par3PacketTypes.Root)]
    [DataRow("PAR LNK", Par3PacketTypes.Link)]
    [DataRow("PAR UNX", Par3PacketTypes.UnixPermissions)]
    [DataRow("PAR FAT", Par3PacketTypes.FatPermissions)]
    public void TypeConstants_AreTheAsciiNamesOfTheSpecification(string ascii, ulong type)
    {
        Assert.AreEqual(type, Par3PacketTypes.FromAscii(ascii));
        Assert.AreEqual(ascii, Par3PacketTypes.ToAscii(type));
    }

    [TestMethod]
    public void Fingerprint_And_SetId_Helpers()
    {
        var bytes = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var fp = Fingerprint.FromBytes(bytes);
        CollectionAssert.AreEqual(bytes, fp.ToArray());
        Assert.AreEqual("0102030405060708090a0b0c0d0e0f10", fp.ToString());
        Assert.IsFalse(fp.IsZero);
        Assert.IsTrue(Fingerprint.Zero.IsZero);
        CollectionAssert.AreEqual(Blake3.Hash("abc"u8)[..16], Fingerprint.Compute("abc"u8).ToArray());
        Assert.AreEqual("1122334455667788", Set.ToString());
        Assert.AreEqual(Set, InputSetId.FromBytes(BitConverter.GetBytes(Set.Value)));
    }

    [TestMethod]
    public void Fingerprint_SortKey_IsBytewiseOrderLikeMemcmp()
    {
        var a = new byte[16];
        a[0] = 1; // 01 00 .. 00: little-endian value 1
        var b = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        b[0] = 0; // 00 FF .. FF: little-endian value 2^128 - 256
        var fa = Fingerprint.FromBytes(a);
        var fb = Fingerprint.FromBytes(b);
        Assert.IsTrue(fa.Value < fb.Value);
        Assert.IsTrue(fa.SortKey > fb.SortKey, "memcmp puts 00 FF.. before 01 00..");
    }

    [TestMethod]
    public void TextPackets_AreUtf8WithoutTerminator()
    {
        Assert.AreEqual("Ünïcødé creator", RoundTrip<Par3CreatorPacket>(Par3PacketWriter.Text(Set, Par3PacketTypes.Creator, "Ünïcødé creator")).Text);
        Assert.AreEqual(string.Empty, RoundTrip<Par3CommentPacket>(Par3PacketWriter.Text(Set, Par3PacketTypes.Comment, string.Empty)).Text);
    }

    [TestMethod]
    [DataRow(new byte[0])]
    [DataRow(new byte[] { 0x1D })]
    [DataRow(new byte[] { 0x0B, 0x10 })]
    [DataRow(new byte[] { 0x8D, 0, 0, 0 })]
    public void Start_Par3cmdlineLayout(byte[] generator)
    {
        var body = Par3PacketWriter.StartBody(new InputSetId(77), Fp(1), 4096, generator);
        Assert.AreEqual(33 + generator.Length, body.Length);
        var s = RoundTrip<Par3StartPacket>(Par3PacketWriter.Build(Set, Par3PacketTypes.Start, body));
        Assert.AreEqual(0UL, s.UniqueNumber);
        Assert.AreEqual(new InputSetId(77), s.ParentSetId);
        Assert.AreEqual(Fp(1), s.ParentRootChecksum);
        Assert.AreEqual(4096UL, s.BlockSize);
        Assert.AreEqual(generator.Length, s.GfSize);
        CollectionAssert.AreEqual(generator, s.GeneratorBytes);
    }

    [TestMethod]
    public void Start_SpecificationLayoutWithUniqueNumber()
    {
        var body = new byte[41 + 2];
        BinaryPrimitives.WriteUInt64LittleEndian(body, 0xDEADBEEF);
        Par3PacketWriter.StartBody(new InputSetId(5), Fp(2), 2048, [0x0B, 0x10]).CopyTo(body, 8);
        var s = RoundTrip<Par3StartPacket>(Par3PacketWriter.Build(Set, Par3PacketTypes.Start, body));
        Assert.AreEqual(0xDEADBEEFUL, s.UniqueNumber);
        Assert.AreEqual(new InputSetId(5), s.ParentSetId);
        Assert.AreEqual(Fp(2), s.ParentRootChecksum);
        Assert.AreEqual(2048UL, s.BlockSize);
        CollectionAssert.AreEqual(new byte[] { 0x0B, 0x10 }, s.GeneratorBytes);
    }

    [TestMethod]
    public void Start_AmbiguousBodyIsResolvedByTheSetId()
    {
        // A specification-layout body with GF(2^16) is 43 bytes; with block size 266 (0x10A) byte 32 is 10, so it also
        // reads as a par3cmdline body with a 10-byte field. The specification's InputSetID = BLAKE3(body) decides.
        var body = new byte[43];
        BinaryPrimitives.WriteUInt64LittleEndian(body, 99);
        Par3PacketWriter.StartBody(default, Fp(3), 266, [0x0B, 0x10]).CopyTo(body, 8);
        Assert.AreEqual(43, 33 + body[32], "fits the par3cmdline layout too");
        using (var dir = new TempDir())
        {
            var specId = InputSetId.FromBytes(Blake3.Hash(body));
            var spec = (Par3StartPacket)Par3PacketReader.ReadFile(dir.Write("s.par3", Par3PacketWriter.Build(specId, Par3PacketTypes.Start, body))).Packets.Single();
            Assert.AreEqual((99UL, 266UL, 2), (spec.UniqueNumber, spec.BlockSize, spec.GfSize));
        }

        // With another set id (as par3cmdline computes it) the same bytes are a par3cmdline body.
        var cmdline = RoundTrip<Par3StartPacket>(Par3PacketWriter.Build(Set, Par3PacketTypes.Start, body));
        Assert.AreEqual((0UL, 10), (cmdline.UniqueNumber, cmdline.GfSize));

        // A par3cmdline body with an 8-byte generator ending in 0 also fits both layouts.
        var gen8 = Par3PacketWriter.StartBody(default, default, 4096, [0x1B, 0, 0, 0, 0, 0, 0, 0]);
        var parsed = RoundTrip<Par3StartPacket>(Par3PacketWriter.Build(Set, Par3PacketTypes.Start, gen8));
        Assert.AreEqual((4096UL, 8), (parsed.BlockSize, parsed.GfSize));
    }

    [TestMethod]
    public void ComputeSetId_IsBlake3OfUniqueNumberAndBody()
    {
        var body = Par3PacketWriter.StartBody(default, default, 64, [0x1D]);
        var expected = InputSetId.FromBytes(Blake3.Hash([.. BitConverter.GetBytes(42UL), .. body]));
        Assert.AreEqual(expected, Par3PacketWriter.ComputeSetId(42, body));
        Assert.AreNotEqual(expected, Par3PacketWriter.ComputeSetId(43, body));
    }

    [TestMethod]
    public void Data_LocatesTheBlockInTheFile()
    {
        var data = TestData.Random(3, 100);
        var header = Par3PacketWriter.DataHeader(Set, 9, data.Length);
        var hasher = new Blake3Hasher();
        hasher.Update(header.AsSpan(24));
        hasher.Update(data);
        hasher.Finalize(header.AsSpan(8, 16));
        var d = RoundTrip<Par3DataPacket>([.. header, .. data]);
        Assert.AreEqual(9UL, d.BlockIndex);
        Assert.AreEqual(13 + 48 + 8, d.DataOffset);
        Assert.AreEqual(100, d.DataLength);
    }

    [TestMethod]
    public void ExternalData_ChecksumTuples()
    {
        var checks = new[] { new BlockChecksum(1, Fp(1)), new BlockChecksum(ulong.MaxValue, Fp(2)) };
        var e = RoundTrip<Par3ExternalDataPacket>(Par3PacketWriter.ExternalData(Set, 1024, checks));
        Assert.AreEqual(1024UL, e.FirstBlockIndex);
        CollectionAssert.AreEqual(checks, e.Checksums.ToArray());
    }

    [TestMethod]
    public void MatrixPackets()
    {
        var c = RoundTrip<Par3CauchyMatrixPacket>(Par3PacketWriter.Cauchy(Set, 3, 6, 12));
        Assert.AreEqual((3UL, 6UL, 12UL), (c.First, c.LastPlus1, c.RecoveryHint));

        var s = RoundTrip<Par3SparseMatrixPacket>(Par3PacketWriter.Sparse(Set, 1, 99, 20, 3, 0xABCDEF));
        Assert.AreEqual((1UL, 99UL, 20UL, 3UL, 0xABCDEFUL), (s.First, s.LastPlus1, s.MaxRecovery, s.NonZeroPerInput, s.Seed));

        var e = RoundTrip<Par3ExplicitMatrixPacket>(Par3PacketWriter.Explicit(Set, [(2, [7]), (5, [9])]));
        CollectionAssert.AreEqual(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 7, 5, 0, 0, 0, 0, 0, 0, 0, 9 }, e.RawBody);
    }

    [TestMethod]
    [DataRow(0u, 17)]
    [DataRow(200u, 18)]
    [DataRow(300u, 19)]
    [DataRow(70_000u, 20)]
    [DataRow(20_000_000u, 21)]
    public void FftMatrix_InterleaveUsesAsFewBytesAsPossible(uint interleave, int bodyLength)
    {
        var packet = Par3PacketWriter.Fft(Set, 0, 0, 5, interleave);
        Assert.AreEqual(48 + bodyLength, packet.Length);
        var f = RoundTrip<Par3FftMatrixPacket>(packet);
        Assert.AreEqual((0UL, 0UL, (byte)5, interleave), (f.First, f.LastPlus1, f.MaxRecoveryLog2, f.Interleave));
    }

    [TestMethod]
    public void RecoveryData_HeaderAndData()
    {
        var data = TestData.Random(4, 64);
        var header = Par3PacketWriter.RecoveryDataHeader(Set, Fp(1), Fp(2), 7, data.Length);
        var packet = (byte[])[.. header, .. data];
        Blake3.Hash(packet.AsSpan(24)).AsSpan(0, 16).CopyTo(packet.AsSpan(8));
        var r = RoundTrip<Par3RecoveryDataPacket>(packet);
        Assert.AreEqual((Fp(1), Fp(2), 7UL), (r.RootChecksum, r.MatrixChecksum, r.RecoveryIndex));
        Assert.AreEqual(13 + 48 + 40, r.DataOffset);
        Assert.AreEqual(64, r.DataLength);
    }

    [TestMethod]
    public void RecoveryExternalData()
    {
        var body = new byte[40 + 24];
        Fp(1).WriteTo(body);
        Fp(2).WriteTo(body.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(40), 99);
        Fp(3).WriteTo(body.AsSpan(48));
        var r = RoundTrip<Par3RecoveryExternalDataPacket>(Par3PacketWriter.Build(Set, Par3PacketTypes.RecoveryExternalData, body));
        Assert.AreEqual((Fp(1), Fp(2), 4UL), (r.RootChecksum, r.MatrixChecksum, r.FirstRecoveryIndex));
        Assert.AreEqual(new BlockChecksum(99, Fp(3)), r.Checksums.Single());
    }

    [TestMethod]
    public void File_NameHashesOptionsAndChunks()
    {
        var chunks = new List<Par3Chunk>
        {
            new() { Length = 2500, FirstBlock = 4, TailCrc = 11, TailHash = Fp(5), TailBlock = 9, TailOffset = 300 },
            new() { Protected = false, Length = 6000 },
            new() { Length = 30, InlineTail = TestData.Random(1, 30) },
        };
        var f = RoundTrip<Par3FilePacket>(Par3PacketWriter.File(Set, "dïr-less.bin", 0x1234, Fp(6), [Fp(7), Fp(8)], chunks, 1000));
        Assert.AreEqual("dïr-less.bin", f.Name);
        Assert.AreEqual(0x1234UL, f.Hash16kCrc);
        Assert.AreEqual(Fp(6), f.FileHash);
        CollectionAssert.AreEqual(new[] { Fp(7), Fp(8) }, f.Options.ToArray());
        var parsed = Par3Chunk.ParseAll(f.ChunkBytes, 1000);
        Assert.AreEqual(3, parsed.Count);
        Assert.AreEqual(chunks[0], parsed[0]);
        Assert.AreEqual(chunks[1], parsed[1]);
        CollectionAssert.AreEqual(chunks[2].InlineTail, parsed[2].InlineTail);
    }

    [TestMethod]
    public void Directory_And_Root_StoreChecksumsInByteOrder()
    {
        var children = Enumerable.Range(0, 6).Select(Fp).ToList();
        var d = RoundTrip<Par3DirectoryPacket>(Par3PacketWriter.Directory(Set, "sub dir", [Fp(20), Fp(21)], children));
        Assert.AreEqual("sub dir", d.Name);
        CollectionAssert.AreEquivalent(new[] { Fp(20), Fp(21) }, d.Options.ToArray());
        CollectionAssert.AreEqual(children.OrderBy(c => Convert.ToHexString(c.ToArray()), StringComparer.Ordinal).ToArray(), d.Children.ToArray());

        var r = RoundTrip<Par3RootPacket>(Par3PacketWriter.Root(Set, 1234, true, [Fp(30)], children));
        Assert.AreEqual(1234UL, r.LowestUnusedBlock);
        Assert.IsTrue(r.IsAbsolute);
        Assert.AreEqual(Fp(30), r.Options.Single());
        CollectionAssert.AreEqual(d.Children.ToArray(), r.Children.ToArray());
        Assert.IsFalse(RoundTrip<Par3RootPacket>(Par3PacketWriter.Root(Set, 0, false, [], [])).IsAbsolute);
    }

    [TestMethod]
    public void Link_RelativeAndAbsolutePaths()
    {
        var rel = Par3PacketWriter.Link(Set, true, "link", ["src", "foo.txt"], false, [Fp(1)]);
        // "src\0foo.txt": NUL separates the path parts, no terminator.
        CollectionAssert.AreEqual("src\0foo.txt"u8.ToArray(), rel.AsSpan(48 + 1 + 2 + 4 + 4, 11).ToArray());
        var l = RoundTrip<Par3LinkPacket>(rel);
        Assert.IsTrue(l.IsSymbolic);
        Assert.AreEqual("link", l.Name);
        CollectionAssert.AreEqual(new[] { "src", "foo.txt" }, l.TargetPath);
        Assert.IsFalse(l.TargetIsAbsolute);
        Assert.AreEqual(Fp(1), l.Options.Single());

        // "/usr/bin/bash" is encoded "\0usr\0bin\0bash".
        var abs = RoundTrip<Par3LinkPacket>(Par3PacketWriter.Link(Set, false, "hard", ["usr", "bin", "bash"], true, []));
        Assert.IsFalse(abs.IsSymbolic);
        Assert.IsTrue(abs.TargetIsAbsolute);
        CollectionAssert.AreEqual(new[] { "usr", "bin", "bash" }, abs.TargetPath);
    }

    [TestMethod]
    public void UnixPermissions_AllFieldsAndXattrs()
    {
        var u = RoundTrip<Par3UnixPermissionsPacket>(Par3PacketWriter.UnixPermissions(Set, 1, -2, 3_000_000_000, 1000, 100, 0x1ED, "dëv", "staff", [("user.a", "1"), ("user.empty", string.Empty)]));
        Assert.AreEqual((1L, -2L, 3_000_000_000L, 1000u, 100u, (ushort)0x1ED), (u.Atime, u.Ctime, u.Mtime, u.Uid, u.Gid, u.Mode));
        Assert.AreEqual(("dëv", "staff"), (u.Owner, u.Group));
        CollectionAssert.AreEqual(new[] { ("user.a", "1"), ("user.empty", string.Empty) }, u.Xattrs.ToArray());

        var unset = RoundTrip<Par3UnixPermissionsPacket>(Par3PacketWriter.UnixPermissions(Set, Par3UnixPermissionsPacket.UnsetTime, Par3UnixPermissionsPacket.UnsetTime,
            Par3UnixPermissionsPacket.UnsetTime, Par3UnixPermissionsPacket.UnsetId, Par3UnixPermissionsPacket.UnsetId, Par3UnixPermissionsPacket.DefaultMode, string.Empty, string.Empty));
        Assert.AreEqual(Par3UnixPermissionsPacket.UnsetTime, unset.Mtime);
        Assert.AreEqual(0, unset.Xattrs.Count);
    }

    [TestMethod]
    public void FatPermissions()
    {
        var f = RoundTrip<Par3FatPermissionsPacket>(Par3PacketWriter.FatPermissions(Set, 1, Par3FatPermissionsPacket.UnsetTime, 3, 0x21));
        Assert.AreEqual((1UL, ulong.MaxValue, 3UL, (ushort)0x21), (f.Creation, f.LastAccess, f.LastWrite, f.Attributes));
    }
}

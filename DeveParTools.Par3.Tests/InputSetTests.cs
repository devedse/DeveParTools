using System.Buffers.Binary;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>Assembling an input set from packets: tree, options, links, lineage, recovery blocks, file discovery.</summary>
[TestClass]
public sealed class InputSetTests
{
    private static Par3InputSet? Assemble(TempDir dir, IEnumerable<byte[]> packets, string name = "s.par3")
        => Par3InputSet.Assemble([Par3PacketReader.ReadFile(Par3SetBuilder.Write(dir[name], packets))]);

    [TestMethod]
    public void NoStartOrNoRoot_GivesNoSet()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        b.Root([b.File("a", new byte[10])]);
        var packets = b.IndexPackets();
        Assert.IsNull(Assemble(dir, packets.Where(p => Type(p) != Par3PacketTypes.Root)));
        Assert.IsNull(Assemble(dir, packets.Where(p => Type(p) != Par3PacketTypes.Start)));
        Assert.IsNotNull(Assemble(dir, packets));

        dir.Write("broken.par3", TestData.Random(1, 100));
        var e = Assert.ThrowsExactly<InvalidDataException>(() => Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["broken.par3"] }));
        StringAssert.Contains(e.Message, "Start and Root");
    }

    private static ulong Type(byte[] packet) => BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(40));

    [TestMethod]
    public void Tree_FilesDirectoriesOptionsAndLinks()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        var unx = b.Add(Par3PacketWriter.UnixPermissions(b.SetId, -1, -1, -1, 1, 2, 0x1A4, "u", "g"));
        var fat = b.Add(Par3PacketWriter.FatPermissions(b.SetId, 1, 2, 3, 0));
        var lnkRoot = b.Add(Par3PacketWriter.Link(b.SetId, true, "top-link", ["a.bin"], false, []));
        var lnkSub = b.Add(Par3PacketWriter.Link(b.SetId, false, "sub-link", ["sub", "b.bin"], false, []));
        var missing = Fingerprint.Compute("not there"u8);
        var a = b.File("a.bin", TestData.Random(1, 200), options: [unx, fat, lnkRoot]);
        var bb = b.File("b.bin", TestData.Random(2, 10), options: [missing]);
        var sub = b.Directory("sub", [bb, missing], [unx, lnkSub]);
        var empty = b.Directory("empty", []);
        b.Root([a, sub, empty], [lnkRoot]);
        var set = Assemble(dir, b.IndexPackets())!;

        CollectionAssert.AreEquivalent(new[] { "a.bin", "sub/b.bin" }, set.Files.Select(f => f.RelativePath).ToArray());
        CollectionAssert.AreEquivalent(new[] { "sub", "empty" }, set.Directories.Select(d => d.RelativePath).ToArray());
        CollectionAssert.AreEquivalent(new[] { "top-link", "sub/sub-link" }, set.Links.Select(l => l.RelativePath).ToArray());

        // A link among a file's options is not a property of the file and is dropped.
        var fa = set.Files.Single(f => f.RelativePath == "a.bin");
        CollectionAssert.AreEquivalent(new[] { typeof(Par3UnixPermissionsPacket), typeof(Par3FatPermissionsPacket) }, fa.Options.Select(o => o.GetType()).ToArray());
        Assert.IsInstanceOfType<Par3UnixPermissionsPacket>(set.Directories.Single(d => d.RelativePath == "sub").Options.Single());

        // Missing option and child packets are reported, the rest is still usable.
        Assert.AreEqual(2, set.Warnings.Count);
        Assert.AreEqual(1, set.Warnings.Count(w => w.StartsWith("Option packet", StringComparison.Ordinal)));
        Assert.AreEqual(1, set.Warnings.Count(w => w.Contains("under 'sub'", StringComparison.Ordinal)));
        Assert.AreEqual("Par3SetBuilder (tests)", set.Creators.Single());
        Assert.AreEqual(0, set.DamagedPackets);
    }

    [TestMethod]
    public void TooDeepTree_IsRejected()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        var child = b.File("leaf", new byte[1]);
        for (int i = 0; i < 300; i++)
        {
            child = b.Directory("d", [child]);
        }

        b.Root([child]);
        Assert.ThrowsExactly<InvalidDataException>(() => Assemble(dir, b.IndexPackets()));
    }

    [TestMethod]
    public void MoreThan2Pow31Blocks_IsNotSupported()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        b.Root([b.File("a", new byte[100])], lowestUnused: 1UL << 31);
        Assert.ThrowsExactly<NotSupportedException>(() => Assemble(dir, b.IndexPackets(externalData: false)));
    }

    public static IEnumerable<object[]> BadChunks()
    {
        // Each File packet has a valid checksum, but chunk descriptions a client cannot use.
        yield return ["truncated", new Par3Chunk[] { new() { Length = 64, FirstBlock = 0 } }, 3];
        yield return ["blocks beyond 2^31", new Par3Chunk[] { new() { Length = 128, FirstBlock = int.MaxValue - 1 } }, 0];
        yield return ["block index wraps around", new Par3Chunk[] { new() { Length = 128, FirstBlock = ulong.MaxValue } }, 0];
        yield return ["tail block beyond 2^31", new Par3Chunk[] { new() { Length = 50, TailBlock = int.MaxValue } }, 0];
        yield return ["tail crosses its block", new Par3Chunk[] { new() { Length = 50, TailBlock = 0, TailOffset = 20 } }, 0];
        yield return ["longer than 2^63", new Par3Chunk[] { new() { Protected = false, Length = long.MaxValue }, new() { Protected = false, Length = 2 } }, 0];
    }

    [TestMethod]
    [DynamicData(nameof(BadChunks))]
    public void UnusableChunkDescriptions_SkipTheFileWithAWarning(string name, Par3Chunk[] chunks, int cut)
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        var good = b.File("good", TestData.Random(1, 100));
        var packet = Par3PacketWriter.File(b.SetId, "bad", 0, default, [], chunks, 64);
        if (cut > 0)
        {
            packet = Par3PacketWriter.Build(b.SetId, Par3PacketTypes.File, packet.AsSpan(48, packet.Length - 48 - cut));
        }

        b.Root([good, b.Add(packet)]);
        var set = Assemble(dir, b.IndexPackets())!;
        Assert.AreEqual("good", set.Files.Single().RelativePath, name);
        StringAssert.StartsWith(set.Warnings.Single(), "File 'bad' is skipped", name);
    }

    [TestMethod]
    public void BlockCount_CoversEveryReferencedBlock()
    {
        // A Root that claims fewer blocks than the files use: the files win.
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        var f = b.File("f", TestData.Random(1, 64 * 3 + 50));
        b.Root([f], lowestUnused: 1);
        var set = Assemble(dir, b.IndexPackets())!;
        Assert.AreEqual(4UL, set.BlockCount);
        Assert.AreEqual(3UL, set.BlockPieces[3].Single().File.Chunks[0].TailBlock);
        Assert.IsTrue(set.BlockPieces[3].Single().IsTail);
    }

    [TestMethod]
    public void RecoveryBlocks_OnlyWithKnownMatrixAndRoot()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        b.Root([b.File("f", TestData.Random(1, 64 * 3))]);
        var cauchy = b.Cauchy([0, 1]);
        var recovery = b.RecoveryPackets();
        var index = b.IndexPackets();

        RecoveryWith(b, out var unknownMatrix, matrix: Fingerprint.Compute("x"u8));
        RecoveryWith(b, out var unknownRoot, root: Fingerprint.Compute("y"u8));
        RecoveryWith(b, out var tooLong, length: 65);
        var set = Assemble(dir, [.. index, .. recovery, .. recovery, unknownMatrix, unknownRoot, tooLong])!;

        // Duplicates count once; unusable recovery packets are ignored.
        CollectionAssert.AreEqual(new ulong[] { 0, 1 }, set.RecoveryBlocks.Select(r => r.Index).ToArray());
        Assert.IsTrue(set.RecoveryBlocks.All(r => r.Matrix.Checksum == cauchy && r.CoveredBlocks == 3));
    }

    private static void RecoveryWith(Par3SetBuilder b, out byte[] packet, Fingerprint? matrix = null, Fingerprint? root = null, int length = 64)
    {
        var body = new byte[40 + length];
        (root ?? b.RootChecksum).WriteTo(body);
        (matrix ?? Par3PacketWriter.ChecksumOf(b.IndexPackets()[1])).WriteTo(body.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(32), 5);
        packet = Par3PacketWriter.Build(b.SetId, Par3PacketTypes.RecoveryData, body);
    }

    [TestMethod]
    public void DataAndExternalDataPackets_FirstUsableOneWins()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        b.Root([b.File("f", TestData.Random(1, 64 * 2))]);
        var bogus = Par3PacketWriter.ExternalData(b.SetId, 1, [new BlockChecksum(1, default), new BlockChecksum(2, default), new BlockChecksum(3, default)]);
        var tooLongData = Par3PacketWriter.Build(b.SetId, Par3PacketTypes.Data, new byte[8 + 65]);
        var set = Assemble(dir, [.. b.IndexPackets(), bogus, tooLongData, b.DataPacket(1), b.DataPacket(1, 10)])!;

        // The real External Data packet came first; entries beyond the block count are ignored.
        Assert.AreEqual(2, set.BlockChecksums.Length);
        Assert.AreNotEqual(1UL, set.BlockChecksums[1]!.Value.Crc64);
        Assert.AreEqual(64, set.DataPackets.Single().Value.DataLength);
    }

    [TestMethod]
    public void IncrementalChild_IsChosenAndIncludesItsParent()
    {
        using var dir = new TempDir();
        var parent = new Par3SetBuilder(64, [0x1D], unique: 1);
        var shared = parent.File("shared.bin", TestData.Random(1, 64 * 2));
        parent.Root([shared]);
        parent.Cauchy([0]);
        Par3SetBuilder.Write(dir["parent.par3"], [.. parent.IndexPackets(), .. parent.RecoveryPackets(), Par3PacketWriter.Text(parent.SetId, Par3PacketTypes.Comment, "old")]);

        var child = new Par3SetBuilder(64, [0x1D], unique: 2, parent: parent.SetId, parentRoot: parent.RootChecksum, firstNewBlock: parent.LowestUnused);
        var added = child.File("new.bin", TestData.Random(2, 64));
        child.Root([shared, added]);
        Par3SetBuilder.Write(dir["child.par3"], [.. child.IndexPackets(), Par3PacketWriter.Text(child.SetId, Par3PacketTypes.Comment, "new")]);

        var set = Par3InputSet.Assemble([Par3PacketReader.ReadFile(dir["parent.par3"]), Par3PacketReader.ReadFile(dir["child.par3"])])!;
        Assert.AreEqual(child.SetId, set.SetId);
        Assert.AreEqual(parent.SetId, set.Start.ParentSetId);
        CollectionAssert.AreEquivalent(new[] { "shared.bin", "new.bin" }, set.Files.Select(f => f.RelativePath).ToArray());
        Assert.AreEqual(3UL, set.BlockCount);
        Assert.AreEqual(2UL, set.Files.Single(f => f.RelativePath == "new.bin").Chunks[0].FirstBlock);

        // The parent's recovery block only covers the parent's blocks (its Root's lowest unused index).
        Assert.AreEqual(2UL, set.RecoveryBlocks.Single().CoveredBlocks);
        CollectionAssert.AreEquivalent(new[] { "old", "new" }, set.Comments);
    }

    [TestMethod]
    public void LineageCycle_Terminates()
    {
        // Set ids are not checked against the Start body, so a crafted pair of sets can name each other as parent.
        using var dir = new TempDir();
        var a = new InputSetId(1);
        var c = new InputSetId(2);
        var file = Par3PacketWriter.File(a, "f", 0, Fingerprint.Compute([]), [], [], 64);
        var packets = new List<byte[]>
        {
            Par3PacketWriter.Build(a, Par3PacketTypes.Start, Par3PacketWriter.StartBody(c, default, 64, [0x1D])),
            Par3PacketWriter.Build(c, Par3PacketTypes.Start, Par3PacketWriter.StartBody(a, default, 64, [0x1D])),
            file,
            Par3PacketWriter.Root(c, 0, false, [], [Par3PacketWriter.ChecksumOf(file)]),
        };
        var set = Assemble(dir, packets)!;
        Assert.AreEqual(c, set.SetId);
        Assert.AreEqual("f", set.Files.Single().RelativePath);
    }

    [TestMethod]
    public void TwoUnrelatedSets_TheOneWithMorePacketsWins()
    {
        using var dir = new TempDir();
        var small = new Par3SetBuilder(64, [0x1D], unique: 1);
        small.Root([small.File("small", new byte[3])]);
        var big = new Par3SetBuilder(64, [0x1D], unique: 2);
        big.Root([big.File("big1", new byte[3]), big.File("big2", new byte[3])]);
        var set = Assemble(dir, [.. small.IndexPackets(), .. big.IndexPackets()])!;
        Assert.AreEqual(big.SetId, set.SetId);
        Assert.AreEqual(2, set.Files.Count);
    }

    [TestMethod]
    public void DiscoverFiles_FindsVolumesOfTheSameBaseName()
    {
        using var dir = new TempDir();
        foreach (var n in new[] { "set.par3", "set.vol00+01.par3", "set.vol1+2.PAR3", "set.part00+04.par3", "settings.par3", "other.par3", "set.txt" })
        {
            dir.Write(n, [1]);
        }

        static string[] Names(string p) => Par3InputSet.DiscoverFiles(p).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        var expected = new[] { "set.par3", "set.part00+04.par3", "set.vol00+01.par3", "set.vol1+2.PAR3" };
        CollectionAssert.AreEqual(expected, Names(dir["set.par3"]));
        CollectionAssert.AreEqual(expected, Names(dir["set.vol1+2.PAR3"]));
        CollectionAssert.AreEqual(expected, Names(dir["set.part00+04.par3"]));
        Assert.AreEqual(dir["set.vol00+01.par3"], Par3InputSet.DiscoverFiles(dir["set.vol00+01.par3"])[0], "the given file comes first");

        var nowhere = Path.Combine(dir.Path, "no-such-dir", "x.par3");
        CollectionAssert.AreEqual(new[] { nowhere }, Par3InputSet.DiscoverFiles(nowhere));
    }
}

using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests;

/// <summary>Assembling a recovery set from packets, and finding the .par2 files that belong together.</summary>
[TestClass]
public sealed class Par2RecoverySetTests
{
    private static readonly Md5Hash IdA = Md5Hash.Compute("file a"u8);
    private static readonly Md5Hash IdB = Md5Hash.Compute("file b"u8);
    private static readonly Md5Hash IdC = Md5Hash.Compute("file c"u8);

    private static Par2FileReadResult Read(string path, params Par2Packet[] packets)
    {
        var r = new Par2FileReadResult { Path = path };
        r.Packets.AddRange(packets);
        return r;
    }

    private static Par2MainPacket MainPacket(Md5Hash set, long sliceSize, params Md5Hash[] ids) => new(set, sliceSize, ids, []);

    private static Par2FileDescriptionPacket Desc(Md5Hash set, Md5Hash id, long length, string name)
        => new(set, id, Md5Hash.Compute(System.Text.Encoding.UTF8.GetBytes(name)), default, length, name);

    private static Par2SliceChecksumPacket Checksums(Md5Hash set, Md5Hash id, int count)
        => new(set, id, Enumerable.Range(0, count).Select(i => (Md5Hash.Compute([(byte)i]), (uint)i)).ToList());

    [TestMethod]
    public void Assemble_WithoutMainPacket_ReturnsNull()
    {
        var set = Md5Hash.Compute("s"u8);
        Assert.IsNull(Par2RecoverySet.Assemble([]));
        Assert.IsNull(Par2RecoverySet.Assemble([Read("a.par2", Desc(set, IdA, 10, "a"), new Par2CreatorPacket(set, "x"))]));
    }

    [TestMethod]
    public void Assemble_NumbersSlicesInMainPacketOrder()
    {
        var s = Md5Hash.Compute("s"u8);
        var files = new[]
        {
            Read("x.par2", MainPacket(s, 100, IdB, IdA, IdC), Desc(s, IdA, 250, "a"), Checksums(s, IdA, 3), Desc(s, IdB, 100, "b"), Checksums(s, IdB, 1)),
            Read("x.vol0+1.par2", Desc(s, IdC, 0, "c"), Checksums(s, IdC, 0)),
        };
        var set = Par2RecoverySet.Assemble(files)!;
        Assert.AreEqual(s, set.SetId);
        Assert.AreEqual(100, set.SliceSize);
        CollectionAssert.AreEqual(new[] { "b", "a", "c" }, set.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 4 }, set.Files.Select(f => f.FirstSlice).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 3, 0 }, set.Files.Select(f => f.SliceCount).ToArray());
        Assert.AreEqual(4, set.TotalSlices);
        Assert.IsTrue(set.Files.All(f => f.HasDescription && f.InRecoverySet && f.SliceChecksums != null));
        CollectionAssert.AreEqual(new[] { "x.par2", "x.vol0+1.par2" }, set.SourceFiles.ToArray());
    }

    [TestMethod]
    public void Assemble_UsesTheSetOfTheFirstFile_EvenWhenAnotherSetHasMorePackets()
    {
        var mine = Md5Hash.Compute("mine"u8);
        var other = Md5Hash.Compute("other"u8);
        var orphan = Md5Hash.Compute("orphan"u8);
        var files = new[]
        {
            // A stray packet of a set without a main packet comes first: it must not decide.
            Read("set.par2", new Par2CreatorPacket(orphan, "stray"), MainPacket(mine, 4, IdA), Desc(mine, IdA, 4, "a")),
            Read("set.other.par2", MainPacket(other, 8, IdB, IdC), Desc(other, IdB, 8, "b"), Desc(other, IdC, 8, "c"), new Par2CreatorPacket(other, "o1"), new Par2CreatorPacket(other, "o2")),
        };
        var set = Par2RecoverySet.Assemble(files)!;
        Assert.AreEqual(mine, set.SetId);
        Assert.AreEqual("a", set.Files.Single().Name);
        Assert.AreEqual(0, set.Creators.Count, "packets of other sets are ignored");
    }

    [TestMethod]
    public void Assemble_FirstCopyOfEachPacketWins_UnicodeNamesOverrideAsciiNames()
    {
        var s = Md5Hash.Compute("s"u8);
        var files = new[]
        {
            Read("a.par2", MainPacket(s, 4, IdA), Desc(s, IdA, 8, "first"), new Par2UnicodeFilenamePacket(s, IdA, "ünicode"), new Par2CommentPacket(s, "c1", false), new Par2CreatorPacket(s, "tool")),
            Read("b.par2", MainPacket(s, 400, IdB), Desc(s, IdA, 8, "second"), new Par2UnicodeFilenamePacket(s, IdA, "other"), new Par2CommentPacket(s, "c1", true), new Par2CommentPacket(s, "c2", false), new Par2CreatorPacket(s, "tool")),
        };
        files[1].DamagedPackets = 3;
        var set = Par2RecoverySet.Assemble(files)!;
        Assert.AreEqual(4, set.SliceSize, "the first main packet");
        Assert.AreEqual("ünicode", set.Files.Single().Name);
        CollectionAssert.AreEqual(new[] { "tool" }, set.Creators);
        CollectionAssert.AreEqual(new[] { "c1", "c2" }, set.Comments);
        Assert.AreEqual(3, set.DamagedPackets);
    }

    [TestMethod]
    public void Assemble_MissingDescriptionOrChecksums()
    {
        var s = Md5Hash.Compute("s"u8);
        var set = Par2RecoverySet.Assemble([Read("a.par2", MainPacket(s, 10, IdA, IdB, IdC), Checksums(s, IdA, 3), Desc(s, IdB, 25, "b"))])!;
        var (a, b, c) = (set.Files[0], set.Files[1], set.Files[2]);

        // No description: no name or length, but the slice count is known from the checksums.
        Assert.IsFalse(a.HasDescription);
        Assert.AreEqual(string.Empty, a.Name);
        Assert.AreEqual(-1, a.Length);
        Assert.AreEqual(3, a.SliceCount);

        // No checksums: everything but per-slice verification.
        Assert.IsTrue(b.HasDescription);
        Assert.IsNull(b.SliceChecksums);
        Assert.AreEqual(3, b.SliceCount);
        Assert.AreEqual(3, b.FirstSlice);

        // Nothing at all: the file has no slices that could be numbered.
        Assert.AreEqual(0, c.SliceCount);
        Assert.AreEqual(6, set.TotalSlices);
    }

    [TestMethod]
    public void Assemble_IgnoresSliceChecksumsWithTheWrongEntryCount()
    {
        var s = Md5Hash.Compute("s"u8);
        var good = Checksums(s, IdA, 3);
        var set = Par2RecoverySet.Assemble([Read("a.par2", MainPacket(s, 10, IdA), Desc(s, IdA, 30, "a"), Checksums(s, IdA, 2), good)])!;
        Assert.AreSame(good.Slices, set.Files[0].SliceChecksums, "the copy that fits the file length is used");

        set = Par2RecoverySet.Assemble([Read("a.par2", MainPacket(s, 10, IdA), Desc(s, IdA, 30, "a"), Checksums(s, IdA, 4))])!;
        Assert.IsNull(set.Files[0].SliceChecksums);
        Assert.AreEqual(3, set.Files[0].SliceCount, "the length decides");
    }

    [TestMethod]
    public void Assemble_NonRecoveryFiles_AreKeptApart()
    {
        var s = Md5Hash.Compute("s"u8);
        var set = Par2RecoverySet.Assemble([Read("a.par2", new Par2MainPacket(s, 10, [IdA], [IdB]), Desc(s, IdA, 5, "a"), Desc(s, IdB, 50, "b"))])!;
        Assert.AreEqual("a", set.Files.Single().Name);
        var non = set.NonRecoveryFiles.Single();
        Assert.AreEqual("b", non.Name);
        Assert.IsFalse(non.InRecoverySet);
        Assert.AreEqual(1, set.TotalSlices, "non-recovery files have no slices in the set");
    }

    [TestMethod]
    public void Assemble_RecoverySlices_ByExponent_OnlyWithTheRightSize()
    {
        var s = Md5Hash.Compute("s"u8);
        var other = Md5Hash.Compute("other"u8);
        var set = Par2RecoverySet.Assemble([Read("a.par2", MainPacket(s, 8, IdA),
            new Par2RecoverySlicePacket(s, 5, "v1", 100, 8),
            new Par2RecoverySlicePacket(s, 5, "v2", 100, 8),
            new Par2RecoverySlicePacket(s, 1, "v1", 200, 8),
            new Par2RecoverySlicePacket(s, 2, "v1", 300, 12),
            new Par2RecoverySlicePacket(other, 3, "v1", 400, 8))])!;
        CollectionAssert.AreEqual(new uint[] { 1, 5 }, set.RecoverySlices.Keys.ToArray());
        Assert.AreEqual("v1", set.RecoverySlices[5].DataFile, "first copy wins");
    }

    [TestMethod]
    [DataRow("set.par2", "set")]
    [DataRow("set.PAR2", "set")]
    [DataRow("set.vol00+01.par2", "set")]
    [DataRow("set.VOL7-12.par2", "set")]
    [DataRow("set.vol3.par2", "set")]
    [DataRow("my.set.vol1+2.par2", "my.set")]
    [DataRow("my.volume.par2", "my.volume")]
    [DataRow("my.vol1+2+3.par2", "my.vol1+2+3")]
    [DataRow("set.vol1+x.par2", "set.vol1+x")]
    [DataRow("a.vol1+2.b.par2", "a.vol1+2.b")]
    [DataRow("noext", "noext")]
    [DataRow("vol1+2.par2", "vol1+2")]
    public void BaseName_StripsOnlyARealVolumeTag(string fileName, string expected)
        => Assert.AreEqual(expected, Par2RecoverySet.BaseNameOf(fileName));

    [TestMethod]
    public void DiscoverFiles_FindsTheSiblingsOfTheSameBaseName()
    {
        using var dir = new TempDir();
        foreach (var n in new[] { "set.par2", "set.vol00+01.par2", "set.vol01+02.PAR2", "set.extra.par2", "setx.par2", "set.par2.bak", "other.vol0+1.par2", "set.vol1+1.par3" })
        {
            dir.Write(n, [1]);
        }

        static string[] Names(string p) => Par2RecoverySet.DiscoverFiles(p).Select(Path.GetFileName).ToArray()!;
        CollectionAssert.AreEqual(new[] { "set.par2", "set.extra.par2", "set.vol00+01.par2", "set.vol01+02.PAR2" }, Names(dir["set.par2"]));
        CollectionAssert.AreEqual(new[] { "set.vol01+02.PAR2", "set.extra.par2", "set.par2", "set.vol00+01.par2" }, Names(dir["set.vol01+02.PAR2"]), "the given file comes first");

        var nowhere = Path.Combine(dir.Path, "missing-dir", "x.par2");
        CollectionAssert.AreEqual(new[] { nowhere }, Par2RecoverySet.DiscoverFiles(nowhere));
    }
}

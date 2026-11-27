using DeveParTools.Core.Operations;
using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests;

/// <summary>Adding recovery volumes to an existing PAR2 set.</summary>
[TestClass]
public sealed class Par2ExtendTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "devepar-p2ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    private string P(string name) => Path.Combine(_dir, name);

    private (string[] Paths, byte[][] Data) Files()
    {
        var data = new[] { TestData.Random(90_000, 1), TestData.Random(33_333, 2), TestData.Random(0, 3) };
        var paths = new[] { P("a.bin"), P("b.bin"), P("empty.txt") };
        for (int i = 0; i < paths.Length; i++)
        {
            File.WriteAllBytes(paths[i], data[i]);
        }

        return (paths, data);
    }

    [TestMethod]
    public void NewVolumes_BelongToTheSet_AndRepairOnTheirOwn()
    {
        var (paths, data) = Files();
        var created = Par2Creator.Create(new Par2CreateOptions { InputFiles = paths, OutputPath = P("set.par2"), SliceSize = 4096, RecoverySliceCount = 4, Comment = "keep me" });
        var index = File.ReadAllBytes(P("set.par2"));

        var extended = Par2Extender.Extend(new Par2ExtendOptions { Par2Path = created.WrittenFiles[^1], RecoverySliceCount = 8, VolumeCount = 2 });
        Assert.AreEqual(created.SetId, extended.SetId);
        Assert.AreEqual(8, extended.RecoverySlices);
        CollectionAssert.AreEqual(new[] { P("set.vol04+4.par2"), P("set.vol08+4.par2") }, extended.WrittenFiles.ToList());
        CollectionAssert.AreEqual(index, File.ReadAllBytes(P("set.par2")), "the index file is untouched");
        var volumePackets = Par2PacketReader.ReadFile(extended.WrittenFiles[0]).Packets;
        Assert.IsTrue(volumePackets.OfType<Par2CommentPacket>().Any(c => c.Text == "keep me"), "the comment is carried over");

        Assert.AreEqual(12, Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = P("set.par2") }).RecoverySlicesAvailable);

        // Only the new volumes: destroy 7 slices' worth of data.
        foreach (var v in created.WrittenFiles.Skip(1))
        {
            File.Delete(v);
        }

        var damaged = (byte[])data[0].Clone();
        Array.Clear(damaged, 4096, 7 * 4096);
        File.WriteAllBytes(paths[0], damaged);
        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = P("set.par2") });
        Assert.IsTrue(verify.RepairPossible);
        Assert.IsTrue(Par2Repairer.Repair(verify, new Par2RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(data[0], File.ReadAllBytes(paths[0]));
    }

    [TestMethod]
    public void Options_FirstExponentOutputAndRedundancy()
    {
        var (paths, _) = Files();
        Par2Creator.Create(new Par2CreateOptions { InputFiles = paths, OutputPath = P("set.par2"), SliceSize = 4096, RecoverySliceCount = 2 });

        var clash = Assert.ThrowsExactly<ArgumentException>(() => Par2Extender.Extend(new Par2ExtendOptions { Par2Path = P("set.par2"), RecoverySliceCount = 3, FirstExponent = 1 }));
        StringAssert.Contains(clash.Message, "Recovery slice 1 already exists");
        Assert.ThrowsExactly<ArgumentException>(() => Par2Extender.Extend(new Par2ExtendOptions { Par2Path = P("set.par2"), RecoverySliceCount = 0 }));

        var more = Par2Extender.Extend(new Par2ExtendOptions { Par2Path = P("set.par2"), RedundancyPercent = 20, FirstExponent = 500, OutputPath = P("extra.par2"), Scheme = RecoveryFileScheme.Uniform });
        Assert.AreEqual(7, more.RecoverySlices, "ceil(32 slices × 20%)");
        StringAssert.EndsWith(more.WrittenFiles.Single(), "extra.vol500+7.par2");

        // The same volume again: refuse to overwrite.
        Assert.ThrowsExactly<IOException>(() => Par2Extender.Extend(new Par2ExtendOptions { Par2Path = P("set.par2"), RedundancyPercent = 20, FirstExponent = 500, OutputPath = P("extra.par2"), Scheme = RecoveryFileScheme.Uniform }));
    }

    [TestMethod]
    public void IncompleteFiles_AreRefused()
    {
        var (paths, _) = Files();
        Par2Creator.Create(new Par2CreateOptions { InputFiles = paths, OutputPath = P("set.par2"), SliceSize = 4096, RecoverySliceCount = 2 });
        File.Delete(paths[1]);
        Assert.ThrowsExactly<InvalidOperationException>(() => Par2Extender.Extend(new Par2ExtendOptions { Par2Path = P("set.par2"), RecoverySliceCount = 1 }));
    }

    [TestMethod]
    public void SetThatCannotBeReproduced_IsRefused_AndNothingIsLeft()
    {
        // Another program's choice: an empty file inside the recovery set (this library puts empty files in the
        // non-recovery set), next to a normal file. Recreating the set gives another set ID.
        var (paths, _) = Files();
        Par2Creator.Create(new Par2CreateOptions { InputFiles = paths[..2], OutputPath = P("set.par2"), SliceSize = 4096, RecoverySliceCount = 2 });
        var packets = PacketSurgery.Split(File.ReadAllBytes(P("set.par2")));
        var main = packets.Single(p => PacketSurgery.Is(p, Par2PacketTypes.Main));
        var emptyHash = Md5Hash.Compute([]);
        var emptyId = Par2PacketWriter.ComputeFileId(emptyHash, 0, "empty.txt");
        var ids = Enumerable.Range(0, 2).Select(i => Md5Hash.FromBytes(PacketSurgery.Body(main).AsSpan(12 + 16 * i, 16))).Append(emptyId).OrderBy(id => id).ToList();
        var body = Par2PacketWriter.MainBody(4096, ids, []);
        var setId = Md5Hash.Compute(body);
        var rebuilt = new List<byte[]> { Par2PacketWriter.Build(setId, Par2PacketTypes.Main, body), Par2PacketWriter.FileDescription(setId, emptyId, emptyHash, emptyHash, 0, "empty.txt") };
        rebuilt.AddRange(packets.Where(p => !PacketSurgery.Is(p, Par2PacketTypes.Main) && !PacketSurgery.Is(p, Par2PacketTypes.Creator))
            .Select(p => Par2PacketWriter.Build(setId, p.AsSpan(48, 16), PacketSurgery.Body(p))));
        File.WriteAllBytes(P("set.par2"), PacketSurgery.Join(rebuilt));
        foreach (var v in Directory.GetFiles(_dir, "set.vol*"))
        {
            File.Delete(v);
        }

        Assert.IsTrue(Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = P("set.par2") }).AllFilesComplete);
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => Par2Extender.Extend(new Par2ExtendOptions { Par2Path = P("set.par2"), RecoverySliceCount = 2 }));
        StringAssert.Contains(ex.Message, "could not be reproduced");
        Assert.AreEqual(0, Directory.GetFiles(_dir, "set.vol*").Length);
    }

    [TestMethod]
    [DataRow("/x/set.vol10+05.par2", "/x/set.par2")]
    [DataRow("/x/set.par2", "/x/set.par2")]
    [DataRow("/x/my.volume.par2", "/x/my.volume.par2")]
    public void IndexPathOf(string input, string expected)
        => Assert.AreEqual(expected.Replace('/', Path.DirectorySeparatorChar), Par2Extender.IndexPathOf(input.Replace('/', Path.DirectorySeparatorChar)));
}

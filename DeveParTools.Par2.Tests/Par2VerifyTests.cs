using DeveParTools.Core.Operations;
using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests;

[TestClass]
public sealed class Par2VerifyTests
{
    private const int SliceSize = 1024;

    /// <summary>a.bin (10 slices, the last one short), b.bin (5 slices), sub/c.bin (3 slices) and "set.par2".</summary>
    private static (Dictionary<string, byte[]> Files, Par2CreateResult Created) MakeSet(TempDir dir, int recovery = 8, int seed = 1)
    {
        var files = TestData.WriteFiles(dir, seed, ("a.bin", 9 * SliceSize + 500), ("b.bin", 5 * SliceSize), ("sub/c.bin", 3 * SliceSize));
        return (files, TestData.Create(dir, files.Keys, SliceSize, recovery));
    }

    private static Par2FileVerification FileOf(Par2VerifyResult r, string name) => r.Files.Single(f => f.File.Name == name);

    private static int Count(OperationContext ctx, BlockState state) => ctx.Blocks!.CountStates()[(int)state];

    [TestMethod]
    public void IntactSet_IsComplete()
    {
        using var dir = new TempDir();
        var (_, created) = MakeSet(dir);
        var observer = new RecordingObserver();
        var ctx = new OperationContext { Observer = observer };
        var r = TestData.Verify(dir, context: ctx);
        Assert.IsTrue(r.AllFilesComplete);
        Assert.IsTrue(r.CriticalDataComplete);
        Assert.IsTrue(r.RepairPossible);
        Assert.AreEqual(0, r.MissingSlices);
        Assert.AreEqual(8, r.RecoverySlicesAvailable);
        Assert.AreEqual(dir.Path, r.BaseDirectory);
        Assert.AreEqual(created.SetId, r.Set.SetId);
        Assert.AreEqual(18, Count(ctx, BlockState.Found));
        Assert.AreEqual(8, ctx.Blocks!.CountRecoveryStates()[(int)RecoveryBlockState.Available]);
        Assert.IsTrue(r.Files.All(f => f.Status == Par2FileStatus.Complete && f.SlicesFound == f.File.SliceCount && f.FoundAs == null));
        Assert.AreEqual(dir["sub/c.bin"], FileOf(r, "sub/c.bin").TargetPath);
        CollectionAssert.AreEqual(new[] { "Loading", "Verifying" }, observer.Stages);
        Assert.IsTrue(observer.Logs.Contains((ParLogLevel.Info, $"Creator: {Par2Creator.DefaultCreator}")));
        Assert.AreEqual((ParLogLevel.Info, "All files are correct."), observer.Logs[^1]);
        Assert.AreEqual(1.0, observer.Fractions[^1], 1e-9);
    }

    [TestMethod]
    public void MissingDamagedTruncatedAndExtendedFiles()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir);
        File.Delete(dir["sub/c.bin"]);
        TestData.Corrupt(dir["b.bin"], 2 * SliceSize + 7);
        File.WriteAllBytes(dir["a.bin"], dir.Read("a.bin").AsSpan(0, 4 * SliceSize + 100).ToArray());

        var observer = new RecordingObserver();
        var ctx = new OperationContext { Observer = observer };
        var r = TestData.Verify(dir, context: ctx);
        Assert.AreEqual(Par2FileStatus.Missing, FileOf(r, "sub/c.bin").Status);
        Assert.AreEqual(Par2FileStatus.Damaged, FileOf(r, "b.bin").Status);
        Assert.AreEqual(Par2FileStatus.Damaged, FileOf(r, "a.bin").Status);
        Assert.AreEqual(4, FileOf(r, "b.bin").SlicesFound);
        Assert.AreEqual(4, FileOf(r, "a.bin").SlicesFound, "slices before the cut");
        Assert.AreEqual(0, FileOf(r, "sub/c.bin").SlicesFound);
        Assert.AreEqual(1 + 6 + 3, r.MissingSlices);
        Assert.IsFalse(r.RepairPossible, "10 missing, 8 recovery slices");
        Assert.AreEqual(10, Count(ctx, BlockState.Unrecoverable));
        StringAssert.Contains(observer.Logs[^1].Message, "10 slice(s) missing, 8 recovery slice(s) available: repair is NOT possible.");

        // Appended data: every slice is in place, but the file is too long.
        File.WriteAllBytes(dir["a.bin"], [.. files[dir["a.bin"]], .. new byte[100]]);
        File.WriteAllBytes(dir["b.bin"], files[dir["b.bin"]]);
        r = TestData.Verify(dir, context: ctx);
        Assert.AreEqual(Par2FileStatus.Damaged, FileOf(r, "a.bin").Status);
        Assert.AreEqual(10, FileOf(r, "a.bin").SlicesFound);
        Assert.AreEqual(3, r.MissingSlices);
        Assert.IsTrue(r.RepairPossible);
        Assert.AreEqual(3, Count(ctx, BlockState.Recoverable));
    }

    [TestMethod]
    public void ShiftedData_IsFoundAtItsNewOffset_IncludingTheShortLastSlice()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir, recovery: 0);
        File.WriteAllBytes(dir["a.bin"], [1, 2, 3, .. files[dir["a.bin"]]]);
        var ctx = new OperationContext();
        var r = TestData.Verify(dir, context: ctx);
        var a = FileOf(r, "a.bin");
        Assert.AreEqual(Par2FileStatus.Damaged, a.Status);
        Assert.AreEqual(10, a.SlicesFound);
        Assert.AreEqual(10, Count(ctx, BlockState.FoundMoved));
        Assert.AreEqual(3 + 9 * SliceSize, r.SliceSources[a.File.FirstSlice + 9]!.Value.Offset);
        Assert.AreEqual(500, r.SliceSources[a.File.FirstSlice + 9]!.Value.Length);
        Assert.IsTrue(r.RepairPossible, "nothing is missing: repair just rewrites the file");
        Assert.IsTrue(Par2Repairer.Repair(r).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void RenamedFiles_AreFoundInExtraFiles()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir, recovery: 0);
        File.Move(dir["b.bin"], dir["renamed-b.bin"]);
        File.Move(dir["sub/c.bin"], dir["c-elsewhere.bin"]);
        dir.Write("same-length-as-b.bin", new byte[5 * SliceSize]);

        var ctx = new OperationContext();
        var r = Par2Verifier.Verify(new Par2VerifyOptions
        {
            Par2Path = dir["set.par2"],
            ExtraDataFiles = [dir["same-length-as-b.bin"], dir["renamed-b.bin"], dir["c-elsewhere.bin"], dir["a.bin"], dir["missing.bin"], dir["renamed-b.bin"]],
        }, ctx);
        Assert.AreEqual(dir["renamed-b.bin"], FileOf(r, "b.bin").FoundAs);
        Assert.AreEqual(dir["c-elsewhere.bin"], FileOf(r, "sub/c.bin").FoundAs);
        Assert.AreEqual(Par2FileStatus.Missing, FileOf(r, "b.bin").Status, "the status describes the target path");
        Assert.AreEqual(0, r.MissingSlices);
        Assert.AreEqual(8, Count(ctx, BlockState.FoundElsewhere));
        Assert.AreEqual(dir["renamed-b.bin"], ctx.Blocks!.FoundAt[FileOf(r, "b.bin").File.FirstSlice].File);

        Assert.IsTrue(Par2Repairer.Repair(r).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void DataOfOneFileInsideAnother_IsFoundElsewhere()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir, recovery: 2);

        // b.bin is gone, but a damaged a.bin happens to contain b.bin's data behind its own.
        File.Delete(dir["b.bin"]);
        File.WriteAllBytes(dir["a.bin"], [.. files[dir["a.bin"]], .. files[dir["b.bin"]]]);
        var ctx = new OperationContext();
        var r = TestData.Verify(dir, context: ctx);
        Assert.AreEqual(5, FileOf(r, "b.bin").SlicesFound);
        Assert.AreEqual(5, Count(ctx, BlockState.FoundElsewhere));
        Assert.AreEqual(1, r.MissingSlices, "only the short last slice of a.bin, now followed by other data");
        Assert.IsTrue(Par2Repairer.Repair(r, new Par2RepairOptions { PurgeBackups = true }).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void IdenticalSlices_OneIntactCopyServesAll()
    {
        using var dir = new TempDir();
        var slice = TestData.Random(SliceSize, 2);
        byte[] data = [.. slice, .. slice, .. slice, .. slice];
        var path = dir.Write("same.bin", data);
        TestData.Create(dir, [path], SliceSize, recovery: 0);
        TestData.Corrupt(path, 2 * SliceSize + 5);

        var ctx = new OperationContext();
        var r = TestData.Verify(dir, context: ctx);
        Assert.AreEqual(0, r.MissingSlices, "slice 2 has the same contents as slice 0");
        Assert.AreEqual(1, Count(ctx, BlockState.FoundMoved));
        Assert.IsTrue(Par2Repairer.Repair(r).Success, "repaired without any recovery slice");
        CollectionAssert.AreEqual(data, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void MissingSliceChecksums_CompleteFilesAreStillComplete()
    {
        // Regression: a file whose checksum packet was lost used to count as damaged, so nothing was repairable.
        using var dir = new TempDir();
        var (files, created) = MakeSet(dir, recovery: 10);
        var bId = Par2PacketReader.ReadFile(dir["set.par2"]).Packets.OfType<Par2FileDescriptionPacket>().Single(d => d.Name == "b.bin").FileId;
        PacketSurgery.RewriteAll(created, p => PacketSurgery.Is(p, Par2PacketTypes.SliceChecksum) && Md5Hash.FromBytes(p.AsSpan(64, 16)) == bId ? [] : [p]);

        var r = TestData.Verify(dir);
        Assert.IsNull(FileOf(r, "b.bin").File.SliceChecksums);
        Assert.IsTrue(r.AllFilesComplete);
        Assert.IsTrue(r.CriticalDataComplete);

        // Damaged without its checksums: none of its slices can be located, so all 5 count as missing.
        TestData.Corrupt(dir["b.bin"], 100);
        TestData.Corrupt(dir["a.bin"], 100);
        r = TestData.Verify(dir);
        Assert.AreEqual(0, FileOf(r, "b.bin").SlicesFound);
        Assert.AreEqual(5 + 1, r.MissingSlices);
        Assert.IsTrue(r.RepairPossible);
        Assert.IsTrue(Par2Repairer.Repair(r).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void SliceChecksumsWithTooFewEntries_DoNotCrashTheVerifier()
    {
        // Regression: an IFSC packet with fewer entries than the file has slices was indexed past its end.
        using var dir = new TempDir();
        var (files, created) = MakeSet(dir, recovery: 10);
        PacketSurgery.RewriteAll(created, p => PacketSurgery.Is(p, Par2PacketTypes.SliceChecksum)
            ? [PacketSurgery.WithBody(p, PacketSurgery.Body(p)[..^20])]
            : [p]);

        var r = TestData.Verify(dir);
        Assert.IsTrue(r.AllFilesComplete, "verified by the whole-file MD5");
        File.Delete(dir["sub/c.bin"]);
        r = TestData.Verify(dir);
        Assert.AreEqual(3, r.MissingSlices);
        Assert.IsTrue(Par2Repairer.Repair(r).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void MissingFileDescriptions_GiveUnknownFiles_AndNoRepair()
    {
        using var dir = new TempDir();
        var (_, created) = MakeSet(dir);
        PacketSurgery.DropAll(created, Par2PacketTypes.FileDescription);
        var r = TestData.Verify(dir);
        Assert.IsTrue(r.Files.All(f => f.Status == Par2FileStatus.Unknown && f.TargetPath.Length == 0));
        Assert.IsFalse(r.CriticalDataComplete);
        Assert.IsFalse(r.RepairPossible);
        Assert.AreEqual(18, r.MissingSlices, "slice counts come from the checksum packets");
        var repair = Par2Repairer.Repair(r);
        Assert.IsFalse(repair.Success);
        StringAssert.Contains(repair.Message, "critical packets");
    }

    [TestMethod]
    public void UnsafeStoredNames_AreIgnoredWithAWarning()
    {
        using var dir = new TempDir();
        var (_, created) = MakeSet(dir);
        PacketSurgery.RewriteAll(created, p =>
        {
            if (!PacketSurgery.Is(p, Par2PacketTypes.FileDescription))
            {
                return [p];
            }

            var d = (Par2FileDescriptionPacket)Par2PacketReader.Parse(PacketSurgery.SetId(p), Par2PacketTypes.FileDescription, PacketSurgery.Body(p))!;
            var name = d.Name == "b.bin" ? "../b.bin" : d.Name;
            return [Par2PacketWriter.FileDescription(d.SetId, d.FileId, d.FileHash, d.Hash16k, d.Length, name)];
        });

        // Verify from a subdirectory, so "../b.bin" would be an existing, intact file.
        Directory.CreateDirectory(dir["v"]);
        foreach (var f in created.WrittenFiles)
        {
            File.Move(f, dir["v/" + Path.GetFileName(f)]);
        }

        var observer = new RecordingObserver();
        var r = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = dir["v/set.par2"], BaseDirectory = dir["v"] }, new OperationContext { Observer = observer });
        var b = FileOf(r, "../b.bin");
        Assert.AreEqual(string.Empty, b.TargetPath);
        Assert.AreEqual(Par2FileStatus.Missing, b.Status, "never looked for outside the base directory");
        Assert.IsTrue(observer.Logs.Any(l => l.Level == ParLogLevel.Warning && l.Message.Contains("Ignoring unsafe file name '../b.bin'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NoMainPacket_Throws()
    {
        using var dir = new TempDir();
        var (_, created) = MakeSet(dir);
        PacketSurgery.DropAll(created, Par2PacketTypes.Main);
        Assert.ThrowsExactly<InvalidDataException>(() => TestData.Verify(dir));
    }

    [TestMethod]
    public void ExtraPar2Files_AddRecoverySlices()
    {
        using var dir = new TempDir();
        var (files, created) = MakeSet(dir, recovery: 3);
        foreach (var (v, i) in created.WrittenFiles.Skip(1).Select((v, i) => (v, i)))
        {
            File.Move(v, dir[$"elsewhere-{i}.bin"]);
        }

        File.Delete(dir["sub/c.bin"]);
        Assert.AreEqual(0, TestData.Verify(dir).RecoverySlicesAvailable);
        var r = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = dir["set.par2"], ExtraPar2Files = [dir["elsewhere-0.bin"], dir["elsewhere-1.bin"], dir["set.par2"]] });
        Assert.AreEqual(3, r.RecoverySlicesAvailable);
        Assert.IsTrue(Par2Repairer.Repair(r).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void DamagedPar2Files_StillWork()
    {
        using var dir = new TempDir();
        var (files, created) = MakeSet(dir, recovery: 7);

        // Destroy the index file completely and damage every volume in the middle: copies of the critical
        // packets in the volumes and the surviving recovery packets are enough.
        File.WriteAllBytes(created.WrittenFiles[0], TestData.Random(5000, 3));
        foreach (var v in created.WrittenFiles.Skip(1))
        {
            TestData.Corrupt(v, 100);
        }

        File.Delete(dir["b.bin"]);
        var r = TestData.Verify(dir);
        Assert.IsTrue(r.Set.DamagedPackets >= 3);
        Assert.AreEqual(4, r.RecoverySlicesAvailable, "the first packet of each of the 3 volumes is lost");
        Assert.IsFalse(r.RepairPossible, "5 slices missing");
        File.WriteAllBytes(dir["b.bin"], files[dir["b.bin"]].AsSpan(0, 2 * SliceSize).ToArray());
        r = TestData.Verify(dir);
        Assert.AreEqual(3, r.MissingSlices);
        Assert.IsTrue(Par2Repairer.Repair(r).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void Cancellation_IsObserved()
    {
        using var dir = new TempDir();
        MakeSet(dir);
        Assert.ThrowsExactly<OperationCanceledException>(() => TestData.Verify(dir, context: new OperationContext { CancellationToken = new CancellationToken(true) }));
    }
}

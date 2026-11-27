using DeveParTools.Core.Operations;
using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests;

[TestClass]
public sealed class Par2RepairTests
{
    private const int SliceSize = 1000;

    private static (Dictionary<string, byte[]> Files, Par2CreateResult Created) MakeSet(TempDir dir, int recovery = 10, int seed = 1)
    {
        var files = TestData.WriteFiles(dir, seed, ("a.bin", 7 * SliceSize + 404), ("b.bin", 3 * SliceSize), ("deep/er/c.bin", 2 * SliceSize + 8));
        return (files, TestData.Create(dir, files.Keys, SliceSize, recovery));
    }

    private static string[] TempFiles(TempDir dir) => Directory.GetFiles(dir.Path, "*.deveparttools.tmp", SearchOption.AllDirectories);

    [TestMethod]
    public void Repair_RebuildsMissingAndDamagedFiles_KeepingBackups()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir);
        Directory.Delete(dir["deep"], recursive: true);
        TestData.Corrupt(dir["a.bin"], 7 * SliceSize + 10);
        var damagedA = dir.Read("a.bin");
        TestData.Corrupt(dir["b.bin"], 0, 2 * SliceSize);

        var observer = new RecordingObserver();
        var ctx = new OperationContext { Observer = observer };
        var verify = TestData.Verify(dir, context: ctx);
        Assert.AreEqual(1 + 2 + 3, verify.MissingSlices);
        var repair = Par2Repairer.Repair(verify, context: ctx);
        Assert.IsTrue(repair.Success, repair.Message);
        Assert.AreEqual("Repaired 3 file(s).", repair.Message);
        Assert.AreEqual(6, repair.SlicesReconstructed);
        CollectionAssert.AreEquivalent(new[] { dir["a.bin"], dir["b.bin"], dir["deep/er/c.bin"] }, repair.RepairedFiles.ToArray());
        Assert.AreEqual(0, repair.FailedFiles.Count);
        TestData.AssertFiles(files);
        Assert.IsTrue(verify.Files.All(f => f.Status == Par2FileStatus.Complete), "statuses are updated");
        Assert.AreEqual(6, ctx.Blocks!.CountStates()[(int)BlockState.Repaired]);
        CollectionAssert.IsSubsetOf(new[] { "Repairing", "Verifying repaired files" }, observer.Stages);

        // The damaged originals are kept as "name.1"; a missing file has no backup.
        CollectionAssert.AreEqual(damagedA, dir.Read("a.bin.1"));
        Assert.IsTrue(File.Exists(dir["b.bin.1"]));
        Assert.IsFalse(File.Exists(dir["deep/er/c.bin.1"]));
        Assert.AreEqual(0, TempFiles(dir).Length);

        // Damaged again: the next backup is "name.2".
        TestData.Corrupt(dir["a.bin"], 5);
        Assert.IsTrue(Par2Repairer.Repair(TestData.Verify(dir)).Success);
        Assert.IsTrue(File.Exists(dir["a.bin.2"]));
        Assert.AreEqual(dir["a.bin.3"], Par2Repairer.NextBackupName(dir["a.bin"]));
    }

    [TestMethod]
    public void PurgeBackups_DeletesTheDamagedOriginals()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir);
        TestData.Corrupt(dir["b.bin"], 1500);
        Assert.IsTrue(Par2Repairer.Repair(TestData.Verify(dir), new Par2RepairOptions { PurgeBackups = true }).Success);
        TestData.AssertFiles(files);
        Assert.IsFalse(File.Exists(dir["b.bin.1"]));
    }

    [TestMethod]
    public void AllFilesComplete_NothingToDo()
    {
        using var dir = new TempDir();
        MakeSet(dir);
        var before = Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories);
        var repair = Par2Repairer.Repair(TestData.Verify(dir));
        Assert.IsTrue(repair.Success);
        Assert.AreEqual("All files are already correct.", repair.Message);
        CollectionAssert.AreEquivalent(before, Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public void NotEnoughRecoverySlices_ChangesNothing()
    {
        using var dir = new TempDir();
        MakeSet(dir, recovery: 2);
        File.Delete(dir["b.bin"]);
        var damaged = dir.Read("a.bin");
        var repair = Par2Repairer.Repair(TestData.Verify(dir));
        Assert.IsFalse(repair.Success);
        Assert.AreEqual("Repair not possible: 3 slice(s) missing but only 2 recovery slice(s) available.", repair.Message);
        Assert.IsFalse(File.Exists(dir["b.bin"]));
        CollectionAssert.AreEqual(damaged, dir.Read("a.bin"));
        Assert.AreEqual(0, TempFiles(dir).Length);
    }

    [TestMethod]
    public void SingularRecoveryMatrix_IsReported_OrAvoidedBySlidingTheWindow()
    {
        // Missing slices 0 and 2 have bases 2^1 and 2^4; with exponents 0 and 21845 the 2×2 matrix is singular,
        // because (2^4 / 2^1)^21845 = 2^65535 = 1 (see Par2Repairer.ChooseRecoverySlices).
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 2, ("x.bin", 3 * SliceSize));
        var x = files.Keys.Single();
        Par2CreateOptions Options(string name, int first) => new() { InputFiles = [x], OutputPath = dir[name], SliceSize = SliceSize, RecoverySliceCount = 1, FirstExponent = first };
        Par2Creator.Create(Options("set.par2", 0));
        Par2Creator.Create(Options("other.par2", 21845));
        Par2Creator.Create(Options("third.par2", 30000));
        TestData.Corrupt(x, 10);
        TestData.Corrupt(x, 2 * SliceSize + 10);
        var damaged = File.ReadAllBytes(x);

        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = dir["set.par2"], ExtraPar2Files = [dir["other.par2"], dir["other.vol21845+1.par2"]] });
        CollectionAssert.AreEqual(new uint[] { 0, 21845 }, verify.Set.RecoverySlices.Keys.ToArray());
        Assert.IsTrue(verify.RepairPossible, "the counts match");
        Assert.IsNull(Par2Repairer.ChooseRecoverySlices(verify.Set, [0, 2], default));
        var repair = Par2Repairer.Repair(verify);
        Assert.IsFalse(repair.Success);
        StringAssert.Contains(repair.Message, "no invertible selection of 2 out of 2 recovery slice(s)");
        CollectionAssert.AreEqual(damaged, File.ReadAllBytes(x), "nothing is written");
        Assert.AreEqual(0, TempFiles(dir).Length);

        // A third exponent: the window {0, 21845} fails, {21845, 30000} works.
        verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = dir["set.par2"], ExtraPar2Files = [dir["other.vol21845+1.par2"], dir["third.vol30000+1.par2"]] });
        Assert.AreEqual(3, verify.RecoverySlicesAvailable);
        CollectionAssert.AreEqual(new uint[] { 21845, 30000 }, Par2Repairer.ChooseRecoverySlices(verify.Set, [0, 2], default)!.Value.Exponents);
        Assert.IsTrue(Par2Repairer.Repair(verify).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void TamperedRecoveryData_FailsTheFinalMd5Check_AndKeepsTheOriginal()
    {
        using var dir = new TempDir();
        var (_, created) = MakeSet(dir, recovery: 3);

        // Change recovery data and re-seal the packet, so the reader accepts it.
        PacketSurgery.RewriteAll(created, p =>
        {
            if (PacketSurgery.Is(p, Par2PacketTypes.RecoverySlice))
            {
                p[100] ^= 0xFF;
                PacketSurgery.Reseal(p);
            }

            return [p];
        });
        TestData.Corrupt(dir["b.bin"], 50);
        var damaged = dir.Read("b.bin");

        var repair = Par2Repairer.Repair(TestData.Verify(dir));
        Assert.IsFalse(repair.Success);
        Assert.AreEqual("1 file(s) failed verification after repair.", repair.Message);
        CollectionAssert.AreEqual(new[] { dir["b.bin"] }, repair.FailedFiles.ToArray());
        Assert.AreEqual(0, repair.RepairedFiles.Count);
        CollectionAssert.AreEqual(damaged, dir.Read("b.bin"));
        Assert.IsFalse(File.Exists(dir["b.bin.1"]));
        Assert.AreEqual(0, TempFiles(dir).Length);
    }

    [TestMethod]
    public void FilesWithUnsafeNames_AreNotRestored_TheOthersAre()
    {
        // Regression: the missing slices of a file that is never written (unsafe name) crashed the reconstruction.
        using var dir = new TempDir();
        var (files, created) = MakeSet(dir, recovery: 12);
        PacketSurgery.RewriteAll(created, p =>
        {
            if (!PacketSurgery.Is(p, Par2PacketTypes.FileDescription))
            {
                return [p];
            }

            var d = (Par2FileDescriptionPacket)Par2PacketReader.Parse(PacketSurgery.SetId(p), Par2PacketTypes.FileDescription, PacketSurgery.Body(p))!;
            return [Par2PacketWriter.FileDescription(d.SetId, d.FileId, d.FileHash, d.Hash16k, d.Length, d.Name == "b.bin" ? "/etc/b.bin" : d.Name)];
        });
        File.Delete(dir["a.bin"]);

        var verify = TestData.Verify(dir);
        Assert.AreEqual(8 + 3, verify.MissingSlices, "b.bin's slices are never looked for");
        var repair = Par2Repairer.Repair(verify);
        Assert.IsFalse(repair.Success);
        Assert.AreEqual("1 file(s) with an unsafe name were not restored: /etc/b.bin.", repair.Message);
        CollectionAssert.AreEqual(new[] { "/etc/b.bin" }, repair.FailedFiles.ToArray());
        CollectionAssert.AreEqual(new[] { dir["a.bin"] }, repair.RepairedFiles.ToArray());
        CollectionAssert.AreEqual(files[dir["a.bin"]], dir.Read("a.bin"));
    }

    [TestMethod]
    public void Cancellation_RemovesTheTemporaryFiles()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir);

        // Shifted by one byte: every slice is found, so no matrix is inverted and the temporary file is written first.
        dir.Write("b.bin", [7, .. files[dir["b.bin"]]]);
        var damaged = dir.Read("b.bin");
        var verify = TestData.Verify(dir);
        Assert.AreEqual(0, verify.MissingSlices);
        Assert.ThrowsExactly<OperationCanceledException>(() => Par2Repairer.Repair(verify, context: new OperationContext { CancellationToken = new CancellationToken(true) }));
        Assert.AreEqual(0, TempFiles(dir).Length);
        CollectionAssert.AreEqual(damaged, dir.Read("b.bin"));
    }

    [TestMethod]
    public void MultiPassReconstruction_WithAShortLastSlice()
    {
        using var dir = new TempDir();
        var (files, _) = MakeSet(dir);
        File.Delete(dir["a.bin"]);
        TestData.Corrupt(dir["deep/er/c.bin"], 2 * SliceSize);

        // 1 byte of budget: 64-byte passes, the last one 40 bytes (1000 = 15·64 + 40); the 404-byte last slice of
        // a.bin and the 8-byte one of c.bin end long before the slice does.
        var ctx = new OperationContext { MemoryBudget = 1, Threads = 2 };
        var repair = Par2Repairer.Repair(TestData.Verify(dir, context: ctx), context: ctx);
        Assert.IsTrue(repair.Success, repair.Message);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void TruncatedInsideAZeroTail_IsRebuiltWithZeros()
    {
        // Regression: the truncated slice still matches (its checksum is over the zero-padded slice) but its
        // source is only 300 bytes long; the rest of the slice was filled from the previous slice's buffer.
        using var dir = new TempDir();
        byte[] data = [.. TestData.Random(SliceSize, 3), .. new byte[SliceSize]];
        var z = dir.Write("z.bin", data);
        TestData.Create(dir, [z], SliceSize, recovery: 1);
        File.WriteAllBytes(z, data.AsSpan(0, SliceSize + 300).ToArray());

        var verify = TestData.Verify(dir);
        Assert.AreEqual(Par2FileStatus.Damaged, verify.Files.Single().Status);
        Assert.AreEqual(0, verify.MissingSlices);
        Assert.AreEqual(300, verify.SliceSources[1]!.Value.Length);
        var repair = Par2Repairer.Repair(verify);
        Assert.IsTrue(repair.Success, repair.Message);
        CollectionAssert.AreEqual(data, File.ReadAllBytes(z));
    }

    [TestMethod]
    public void NextBackupName_SkipsExistingBackups()
    {
        using var dir = new TempDir();
        var p = dir.Write("f.bin", [1]);
        Assert.AreEqual(p + ".1", Par2Repairer.NextBackupName(p));
        dir.Write("f.bin.1", [1]);
        dir.Write("f.bin.2", [1]);
        Assert.AreEqual(p + ".3", Par2Repairer.NextBackupName(p));
    }
}

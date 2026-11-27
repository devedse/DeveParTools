using DeveParTools.Core.Operations;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>Every PAR3 operation announces its steps, runs them in that order, and reports progress for each.</summary>
[TestClass]
public sealed class ProgressTests
{
    private static (TempDir Dir, List<byte[]> Contents, string[] Paths) Files(int count = 3, int size = 300_000)
    {
        var dir = new TempDir();
        var contents = Enumerable.Range(0, count).Select(i => TestData.Random(900 + i, size - i * 1234)).ToList();
        var paths = contents.Select((c, i) => dir.Write($"f{i}.bin", c)).ToArray();
        return (dir, contents, paths);
    }

    [TestMethod]
    [DataRow(Par3Code.Fft, false)]
    [DataRow(Par3Code.Cauchy, true)]
    public void Create_Verify_Repair_FollowTheirPlans(Par3Code code, bool storeData)
    {
        var (dir, contents, paths) = Files();
        using (dir)
        {
            var create = new RecordingObserver();
            Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 1024, RedundancyPercent = 10, Code = code, StoreData = storeData }, new OperationContext { Observer = create });
            string[] expected = storeData
                ? [Par3Stages.Preparing, Par3Stages.Hashing, Par3Stages.Metadata, Par3Stages.Encoding, Par3Stages.WritingRecovery, Par3Stages.StoringData, Par3Stages.WritingIndex]
                : [Par3Stages.Preparing, Par3Stages.Hashing, Par3Stages.Metadata, Par3Stages.Encoding, Par3Stages.WritingRecovery, Par3Stages.WritingIndex];
            CollectionAssert.AreEqual(expected, create.Stages);
            create.AssertStagesFollowThePlan();
            Assert.IsTrue(create.Fractions[Par3Stages.Encoding].Count > 1, "encoding reports as it goes");
            Assert.AreEqual(1.0, create.Fractions[Par3Stages.WritingRecovery][^1], 1e-9);
            StringAssert.Contains(create.Plans[^1].Single(p => p.Name == Par3Stages.Encoding).Description, code == Par3Code.Fft ? "FFT" : "Cauchy");

            // Damage a file (shift its data) so verify also scans, then repair.
            var shifted = new byte[50].Concat(contents[0]).ToArray();
            File.WriteAllBytes(paths[0], shifted.AsSpan(0, contents[0].Length).ToArray());
            var verifying = new RecordingObserver();
            var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["set.par3"] }, new OperationContext { Observer = verifying });
            CollectionAssert.AreEqual(new[] { VerifyStages.Loading, VerifyStages.Verifying, VerifyStages.Scanning }, verifying.Stages, "the damaged file is searched for its moved blocks");
            verifying.AssertStagesFollowThePlan();
            Assert.IsTrue(verifying.Plans[^1].Single(p => p.Name == VerifyStages.Scanning).Weight > 0, "scanning was planned once it was needed");
            Assert.AreEqual(0, verifying.Plans[0].Single(p => p.Name == VerifyStages.Scanning).Weight, "not before");

            var repairing = new RecordingObserver();
            var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true, RestorePermissions = true }, new OperationContext { Observer = repairing });
            Assert.IsTrue(repair.Success, repair.Message);
            // With stored data the blocks are all in the .part files: nothing to reconstruct, only to write.
            CollectionAssert.AreEqual(storeData ? new[] { RepairStages.Writing, RepairStages.Metadata } : new[] { RepairStages.Reconstructing, RepairStages.Checking, RepairStages.Writing, RepairStages.Metadata }, repairing.Stages);
            repairing.AssertStagesFollowThePlan();
            CollectionAssert.AreEqual(contents[0], dir.Read("f0.bin"));
        }
    }

    [TestMethod]
    public void Extend_AnnouncesVerifyAndEncoding_AndPassesTheBlockMapOn()
    {
        var (dir, _, paths) = Files(2, 100_000);
        using (dir)
        {
            Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 1024, RecoveryBlockCount = 4, Code = Par3Code.Cauchy });
            var observer = new RecordingObserver();
            BlockStateMap? map = null;
            var context = new OperationContext { Observer = observer };
            context.BlocksCreated += m => map = m;
            Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 3 }, context);
            CollectionAssert.AreEqual(new[] { VerifyStages.Loading, VerifyStages.Verifying, Par3Stages.Encoding, Par3Stages.WritingRecovery }, observer.Stages);
            Assert.HasCount(2, observer.Plans, "the inner verify's own plan is not passed on; the extend plan is refined once");
            observer.AssertStagesFollowThePlan();
            Assert.IsNotNull(map);
            Assert.AreSame(map, context.Blocks);
        }
    }

    [TestMethod]
    public void InsertAndDelete_FollowTheirPlans()
    {
        using var dir = new TempDir();
        var zip = dir.Write("a.zip", InsideTests.MakeZip(5, files: 2, size: 600_000));
        var inserting = new RecordingObserver();
        Par3Inside.Insert(new Par3InsideOptions { ArchivePath = zip, RedundancyPercent = 5 }, new OperationContext { Observer = inserting });
        CollectionAssert.AreEqual(new[] { "Planning", "Hashing", "Writing recovery data" }, inserting.Stages);
        inserting.AssertStagesFollowThePlan();

        var deleting = new RecordingObserver();
        Par3Inside.Delete(zip, new OperationContext { Observer = deleting });
        CollectionAssert.AreEqual(new[] { VerifyStages.Loading, VerifyStages.Verifying, "Removing recovery data" }, deleting.Stages);
        deleting.AssertStagesFollowThePlan();
    }

    [TestMethod]
    public void ReadFile_ReportsHowFarItGot_InLargeFiles()
    {
        using var dir = new TempDir();
        var data = dir.Write("big.bin", TestData.Random(77, 6 << 20));
        var created = Par3Creator.Create(new Par3CreateOptions { Inputs = [data], OutputPath = dir["set.par3"], BlockSize = 65536, RecoveryBlockCount = 80, VolumeCount = 1, Code = Par3Code.Cauchy });
        var volume = created.WrittenFiles[1];
        var positions = new List<long>();
        DeveParTools.Par3.Packets.Par3PacketReader.ReadFile(volume, default, positions.Add);
        Assert.IsTrue(positions.Count >= 1 && positions.SequenceEqual(positions.Order()) && positions[^1] < new FileInfo(volume).Length, string.Join(", ", positions));
    }

    [TestMethod]
    public void Repair_ManyMissingBlocks_ReportsCheckingProgress()
    {
        using var dir = new TempDir();
        var content = TestData.Random(31, 300 * 1024);
        var path = dir.Write("a.bin", content);
        Par3Creator.Create(new Par3CreateOptions { Inputs = [path], OutputPath = dir["set.par3"], BlockSize = 1024, RecoveryBlockCount = 300, Code = Par3Code.Fft });
        File.Delete(path);
        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["set.par3"] });
        var observer = new RecordingObserver();
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }, new OperationContext { Observer = observer });
        Assert.IsTrue(repair.Success, repair.Message);
        Assert.IsTrue(observer.Fractions[RepairStages.Checking].Any(f => f is > 0 and < 1), "300 rebuilt blocks are checked with progress");
        CollectionAssert.AreEqual(content, dir.Read("a.bin"));
    }
}

/// <summary>The FFT code in passes (a slice of every block at a time): what each pass reports, and the checksums.</summary>
[TestClass]
public sealed class FftPassTests
{
    [TestMethod]
    public void CreateAndRepair_InPasses_SayWhichPassAndWhatPart_AndTheSetIsCorrect()
    {
        using var dir = new TempDir();
        var content = TestData.Random(41, 400 * 1024);
        var path = dir.Write("a.bin", content);

        // A small memory budget: blocks of 4 KiB are encoded in slices, a few passes.
        var context = new OperationContext { Observer = new RecordingObserver(), MemoryBudget = 200_000 };
        var create = (RecordingObserver)context.Observer;
        Par3Creator.Create(new Par3CreateOptions { Inputs = [path], OutputPath = dir["set.par3"], BlockSize = 4096, RecoveryBlockCount = 40, Code = Par3Code.Fft }, context);

        var encoding = create.Details[Par3Stages.Encoding];
        int passes = int.Parse(encoding[0].Split(' ')[3].TrimEnd(':'), System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(passes > 1, encoding[0]);
        StringAssert.StartsWith(encoding[0], $"pass 1 of {passes}: reading the input blocks (");
        CollectionAssert.Contains(encoding, $"pass 1 of {passes}: computing the recovery data");
        CollectionAssert.Contains(encoding, $"pass {passes} of {passes}: writing the recovery data (40 of 40)");
        var fractions = create.Fractions[Par3Stages.Encoding];
        Assert.IsTrue(fractions.Zip(fractions.Skip(1)).All(p => p.Second >= p.First), "the encoding bar never goes back");
        Assert.AreEqual(1.0, fractions[^1], 1e-9);
        CollectionAssert.Contains(create.Details[Par3Stages.WritingRecovery], "recovery block headers (40 of 40)");
        StringAssert.StartsWith(create.Details[Par3Stages.WritingRecovery][^1], "copies of the set's description (recovery file ");

        // The checksums were built while writing (no read back): every recovery block is accepted.
        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["set.par3"] });
        Assert.IsTrue(verify.AllFilesComplete);
        Assert.AreEqual(40, verify.RecoveryBlocksAvailable);

        // Repair in passes too.
        var damaged = (byte[])content.Clone();
        Array.Clear(damaged, 10 * 4096, 30 * 4096);
        File.WriteAllBytes(path, damaged);
        var repairing = new RecordingObserver();
        var report = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["set.par3"] });
        var repair = Par3Repairer.Repair(report, new Par3RepairOptions { PurgeBackups = true }, new OperationContext { Observer = repairing, MemoryBudget = 200_000 });
        Assert.IsTrue(repair.Success, repair.Message);
        CollectionAssert.AreEqual(content, dir.Read("a.bin"));
        var rebuilding = repairing.Details[RepairStages.Reconstructing];
        Assert.IsTrue(rebuilding.Any(d => d.EndsWith(": computing the missing blocks", StringComparison.Ordinal)), string.Join(" | ", rebuilding));
        Assert.IsTrue(rebuilding.Any(d => d.Contains(": writing the rebuilt blocks (30 of 30)", StringComparison.Ordinal)), string.Join(" | ", rebuilding));
        Assert.IsTrue(rebuilding.Any(d => d.Contains(": reading the input blocks (", StringComparison.Ordinal)), string.Join(" | ", rebuilding));
    }

    [TestMethod]
    public void PassProgress_SharesFollowTheMeasuredTimes_AndTheValueNeverGoesBack()
    {
        var reports = new DetailRecorder();
        var progress = new FftPassProgress(reports, 2, "computing the recovery data", "writing the recovery data");
        CollectionAssert.AreEqual(new[] { 0.5, 0.3, 0.2 }, progress.Shares.ToArray(), "a guess for the first pass");
        progress.StartPass();
        progress.Reading(1, 2);
        progress.Begin(FftPassProgress.Phase.Computing);
        Thread.Sleep(60); // computing takes longest
        progress.Computing(0.5);
        progress.Computing(0.25); // a late report from another thread
        progress.Begin(FftPassProgress.Phase.Writing);
        progress.Writing(1, 1);
        progress.EndPass();
        Assert.AreEqual(0.5, reports.Values[^1], 1e-12, "a pass is exactly 1/passes");

        progress.StartPass();
        Assert.IsTrue(progress.Shares[1] > progress.Shares[0] && progress.Shares[1] > progress.Shares[2] && Math.Abs(progress.Shares.Sum() - 1) < 1e-9,
            "from the second pass on, the shares are the measured times: " + string.Join(", ", progress.Shares));
        progress.Writing(1, 1);
        progress.EndPass();
        Assert.AreEqual(1.0, reports.Values[^1], 1e-12);
        Assert.IsTrue(reports.Values.Zip(reports.Values.Skip(1)).All(p => p.Second >= p.First), string.Join(", ", reports.Values));
        Assert.AreEqual("pass 1 of 2: reading the input blocks (1 of 2)", reports.Details[0]);
        Assert.AreEqual("pass 1 of 2: computing the recovery data", reports.Details[1]);
        Assert.AreEqual("pass 2 of 2: writing the recovery data (1 of 1)", reports.Details[^1]);

        var single = new DetailRecorder();
        var one = new FftPassProgress(single, 1, "computing the missing blocks", "writing the rebuilt blocks");
        one.StartPass();
        one.Computing(1);
        Assert.AreEqual("computing the missing blocks", single.Details[0], "one pass: no pass number");
        new FftPassProgress(null, 1, "c", "w").Computing(0.5); // nobody listens
    }

    [TestMethod]
    public void RecoverySink_BuildsTheChecksumWhileWriting_OrReadsBackWhenOutOfOrder()
    {
        using var dir = new TempDir();
        const int BlockSize = 1000;
        var data = TestData.Random(7, BlockSize);
        byte[] Checksum(bool inOrder, out long readBack)
        {
            using var stream = new FileStream(dir[inOrder ? "in-order.bin" : "out-of-order.bin"], FileMode.Create, FileAccess.ReadWrite);
            var header = Enumerable.Range(0, 88).Select(i => (byte)i).ToArray();
            stream.SetLength(header.Length + BlockSize);
            var sink = new Par3Creator.RecoverySink([(stream, header.Length, header)], BlockSize, new BlockStateMap(1, 1));
            (int Offset, int Length)[] slices = inOrder ? [(0, 400), (400, 400), (800, 200)] : [(0, 400), (800, 200), (400, 400)];
            foreach (var (offset, length) in slices)
            {
                sink.Write(0, offset, data.AsSpan(offset, length));
            }

            Assert.AreEqual(inOrder ? 0 : BlockSize, sink.ReadBackBytes(0));
            readBack = sink.Complete(0);
            Assert.AreEqual(0, sink.Complete(0), "complete now");
            return header[8..24];
        }

        var built = Checksum(inOrder: true, out long none);
        var readBackSum = Checksum(inOrder: false, out long read);
        Assert.AreEqual(0, none);
        Assert.AreEqual(BlockSize, read);
        CollectionAssert.AreEqual(built, readBackSum, "the same checksum either way");
        var expected = new byte[16];
        var hasher = new Core.Hashing.Blake3Hasher();
        hasher.Update(Enumerable.Range(0, 88).Select(i => (byte)i).ToArray().AsSpan(24));
        hasher.Update(data);
        hasher.Finalize(expected);
        CollectionAssert.AreEqual(expected, built);
    }

    private sealed class DetailRecorder : IDetailedProgress
    {
        public List<double> Values { get; } = [];

        public List<string> Details { get; } = [];

        public void Report(double value) => Values.Add(value);

        public void Report(double value, string? detail)
        {
            Values.Add(value);
            if (detail != null)
            {
                Details.Add(detail);
            }
        }
    }
}

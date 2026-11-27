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
}

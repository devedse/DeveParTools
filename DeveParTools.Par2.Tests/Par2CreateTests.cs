using DeveParTools.Core.Operations;
using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests;

[TestClass]
public sealed class Par2CreateTests
{
    private static List<Par2Packet> Packets(string path) => Par2PacketReader.ReadFile(path).Packets;

    [TestMethod]
    [DataRow(RecoveryFileScheme.Exponential, 10, 0, 0, "vol00+1 vol01+2 vol03+4 vol07+3", DisplayName = "exponential (par2cmdline default)")]
    [DataRow(RecoveryFileScheme.Exponential, 10, 2, 0, "vol00+4 vol04+6", DisplayName = "exponential with 2 files (par2cmdline -n2)")]
    [DataRow(RecoveryFileScheme.Exponential, 3, 3, 0, "vol0+1 vol1+2", DisplayName = "exponential: no empty files")]
    [DataRow(RecoveryFileScheme.Exponential, 4, 0, 98, "vol098+1 vol099+2 vol101+1", DisplayName = "exponential from exponent 98 (digits of 102)")]
    [DataRow(RecoveryFileScheme.Exponential, 1, 1, 0, "vol0+1", DisplayName = "one slice")]
    [DataRow(RecoveryFileScheme.Uniform, 10, 3, 0, "vol00+4 vol04+3 vol07+3", DisplayName = "uniform with 3 files")]
    [DataRow(RecoveryFileScheme.Uniform, 10, 0, 0, "vol00+10", DisplayName = "uniform without a count: one file")]
    [DataRow(RecoveryFileScheme.Uniform, 2, 5, 0, "vol0+1 vol1+1", DisplayName = "uniform: at most one file per slice")]
    [DataRow(RecoveryFileScheme.LimitSize, 10, 0, 0, "vol00+1 vol01+2 vol03+3 vol06+3 vol09+1", DisplayName = "limited to the largest file (3 slices)")]
    [DataRow(RecoveryFileScheme.LimitSize, 3, 7, 0, "vol0+1 vol1+2", DisplayName = "limit: the volume count is not used")]
    public void VolumeSchemes_DistributeAndNameTheVolumes(RecoveryFileScheme scheme, int recovery, int volumeCount, int firstExponent, string expected)
    {
        using var dir = new TempDir();

        // Largest file: 3 full slices plus a bit, so LimitSize allows 3 slices (12000 / 4000) per file.
        var files = TestData.WriteFiles(dir, 1, ("big.bin", 12_500), ("small.bin", 3000));
        var result = Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = files.Keys.ToList(),
            OutputPath = dir["set.par2"],
            SliceSize = 4000,
            RecoverySliceCount = recovery,
            FirstExponent = firstExponent,
            Scheme = scheme,
            VolumeCount = volumeCount == 0 ? null : volumeCount,
        });
        var names = result.WrittenFiles.Skip(1).Select(f => Path.GetFileName(f)["set.".Length..^".par2".Length]).ToArray();
        CollectionAssert.AreEqual(expected.Split(' '), names);
        Assert.AreEqual(dir["set.par2"], result.WrittenFiles[0]);
        Assert.AreEqual(recovery, result.RecoverySlices);

        // Each volume holds exactly its exponents, and the whole set verifies.
        var exponents = result.WrittenFiles.Skip(1).SelectMany(f => Packets(f).OfType<Par2RecoverySlicePacket>().Select(r => (int)r.Exponent)).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(firstExponent, recovery).ToArray(), exponents);
        var verify = TestData.Verify(dir);
        Assert.IsTrue(verify.AllFilesComplete);
        Assert.AreEqual(recovery, verify.RecoverySlicesAvailable);
    }

    [TestMethod]
    public void Create_WritesTheCriticalPackets_AndCopiesThemIntoEveryVolume()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 2, ("a.bin", 10_000), ("sub/b.bin", 5_000), ("empty.bin", 0));
        var result = Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = files.Keys.ToList(),
            OutputPath = dir["set.par2"],
            SliceSize = 1024,
            RecoverySliceCount = 7,
            Comment = "a comment",
            Creator = "test creator",
        });
        Assert.AreEqual(1024, result.SliceSize);
        Assert.AreEqual(10 + 5, result.InputSlices);

        var index = Packets(dir["set.par2"]);
        var main = index.OfType<Par2MainPacket>().Single();
        Assert.AreEqual(result.SetId, main.SetId);
        Assert.IsTrue(index.All(p => p.SetId == result.SetId));
        CollectionAssert.AreEqual(main.RecoveryFileIds.Order().ToArray(), main.RecoveryFileIds.ToArray(), "file IDs are sorted");
        Assert.AreEqual(result.SetId, Md5Hash.Compute(Par2PacketWriter.MainBody(1024, main.RecoveryFileIds, main.NonRecoveryFileIds)), "set ID = MD5 of the main packet body");
        var descriptions = index.OfType<Par2FileDescriptionPacket>().ToDictionary(d => d.FileId, d => d.Name);
        CollectionAssert.AreEquivalent(new[] { "a.bin", "sub/b.bin" }, main.RecoveryFileIds.Select(id => descriptions[id]).ToArray());

        // The empty file has no slices: it is in the non-recovery set (par2cmdline skips empty inputs and would call
        // an empty recovery-set file damaged), described but without slice checksums.
        Assert.AreEqual("empty.bin", descriptions[main.NonRecoveryFileIds.Single()]);
        Assert.AreEqual(2, index.OfType<Par2SliceChecksumPacket>().Count());
        Assert.AreEqual("a comment", index.OfType<Par2CommentPacket>().Single().Text);
        Assert.AreEqual("test creator", index.OfType<Par2CreatorPacket>().Single().Text);
        Assert.AreEqual(0, index.OfType<Par2RecoverySlicePacket>().Count());

        // Volumes 1, 2, 4: 1 + log2(count) copies of each critical packet, plus one creator packet.
        foreach (var (path, copies) in result.WrittenFiles.Skip(1).Zip(new[] { 1, 2, 3 }))
        {
            var p = Packets(path);
            Assert.AreEqual(copies, p.OfType<Par2MainPacket>().Count(), path);
            Assert.AreEqual(3 * copies, p.OfType<Par2FileDescriptionPacket>().Count(), path);
            Assert.AreEqual(1, p.OfType<Par2CreatorPacket>().Count(), path);
        }
    }

    [TestMethod]
    public void RecoveryData_IsTheGf16CombinationOfTheSlices()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 3, ("x", 20), ("y", 7));
        var result = Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["s.par2"], SliceSize = 8, RecoverySliceCount = 3, FirstExponent = 1000 });

        // Input slices in set order (files by File ID, zero-padded to 8 bytes = 4 words).
        var main = Packets(dir["s.par2"]).OfType<Par2MainPacket>().Single();
        var names = Packets(dir["s.par2"]).OfType<Par2FileDescriptionPacket>().ToDictionary(d => d.FileId, d => d.Name);
        var slices = new List<byte[]>();
        foreach (var id in main.RecoveryFileIds)
        {
            var data = File.ReadAllBytes(dir[names[id]]);
            for (int o = 0; o < data.Length; o += 8)
            {
                var s = new byte[8];
                data.AsSpan(o, Math.Min(8, data.Length - o)).CopyTo(s);
                slices.Add(s);
            }
        }

        Assert.AreEqual(result.InputSlices, slices.Count);
        var f = Par2Math.Field;
        foreach (var r in result.WrittenFiles.Skip(1).SelectMany(v => Packets(v).OfType<Par2RecoverySlicePacket>()))
        {
            var actual = File.ReadAllBytes(r.DataFile).AsSpan((int)r.DataOffset, (int)r.DataLength).ToArray();
            var expected = new byte[8];
            for (int w = 0; w < 4; w++)
            {
                ushort sum = 0;
                for (int i = 0; i < slices.Count; i++)
                {
                    sum ^= f.Mul(Par2Math.Coefficient(i, r.Exponent), BitConverter.ToUInt16(slices[i], 2 * w));
                }

                BitConverter.GetBytes(sum).CopyTo(expected, 2 * w);
            }

            CollectionAssert.AreEqual(expected, actual, $"exponent {r.Exponent}");
        }
    }

    [TestMethod]
    [DataRow(4096L, 1L, DisplayName = "64-byte passes")]
    [DataRow((1L << 20) + 4, 2L << 20, DisplayName = "slices over 1 MiB, packet MD5 read back in chunks")]
    public void MultiPassEncoding_GivesTheSameFilesAsOnePass(long sliceSize, long memoryBudget)
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 4, ("a.bin", (int)(2.5 * sliceSize)), ("b.bin", (int)sliceSize / 3));
        var onePass = TestData.Create(dir, files.Keys, sliceSize, 3, "one.par2");
        var observer = new RecordingObserver();
        var multi = TestData.Create(dir, files.Keys, sliceSize, 3, "multi.par2", new OperationContext { MemoryBudget = memoryBudget, Observer = observer });
        Assert.IsTrue(observer.Stages.Contains("Hashing"), "multi-pass encoding hashes in a separate pass");
        observer.AssertStagesFollowThePlan();
        StringAssert.Contains(observer.Plans[^1].Single(p => p.Name == Par2Stages.Encoding).Description, "times, one part each time");

        foreach (var (a, b) in onePass.WrittenFiles.Zip(multi.WrittenFiles))
        {
            CollectionAssert.AreEqual(File.ReadAllBytes(a), File.ReadAllBytes(b), b);
        }

        File.Delete(files.Keys.First());
        var verify = TestData.Verify(dir, "multi.par2");
        Assert.IsTrue(Par2Repairer.Repair(verify).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void NoRecoverySlices_WritesOnlyTheIndex_WithCorrectHashes()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 5, ("a.bin", 9000), ("b.bin", 100));
        var observer = new RecordingObserver();
        var result = Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["set.par2"], SliceSize = 1000, RecoverySliceCount = 0 }, new OperationContext { Observer = observer });
        CollectionAssert.AreEqual(new[] { dir["set.par2"] }, result.WrittenFiles.ToArray());
        CollectionAssert.AreEqual(new[] { "Preparing", "Hashing", "Writing the index file" }, observer.Stages);
        observer.AssertStagesFollowThePlan();
        Assert.AreEqual(1.0, observer.Fractions[^1]);
        Assert.IsTrue(TestData.Verify(dir).AllFilesComplete);

        TestData.Corrupt(files.Keys.First(), 4500);
        var damaged = TestData.Verify(dir);
        Assert.AreEqual(1, damaged.MissingSlices);
        Assert.IsFalse(damaged.RepairPossible);
    }

    [TestMethod]
    public void OnlyEmptyFiles_GiveASetWithoutSlices()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 6, ("e1", 0), ("e2", 0));
        var result = Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["set.par2"], RecoverySliceCount = 5 });
        Assert.AreEqual(0, result.InputSlices);
        Assert.AreEqual(0, result.RecoverySlices, "nothing to protect");
        Assert.AreEqual(4, result.SliceSize);

        File.Delete(files.Keys.First());
        var verify = TestData.Verify(dir);
        Assert.IsTrue(verify.RepairPossible);
        Assert.IsTrue(Par2Repairer.Repair(verify).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void OutputWithoutPar2Extension_IsUsedAsIs_AndVolumesAreFoundFromIt()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 14, ("a.bin", 1000));
        var result = Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["recovery"], SliceSize = 100, RecoverySliceCount = 3 });
        CollectionAssert.AreEqual(new[] { "recovery", "recovery.vol0+1.par2", "recovery.vol1+2.par2" }, result.WrittenFiles.Select(Path.GetFileName).ToArray());

        TestData.Corrupt(files.Keys.Single(), 0, 300);
        var verify = TestData.Verify(dir, "recovery");
        Assert.AreEqual(3, verify.RecoverySlicesAvailable);
        Assert.IsTrue(Par2Repairer.Repair(verify).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void TheSameFileGivenTwice_IsUsedOnce()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 7, ("a.bin", 100));
        var a = files.Keys.Single();
        var result = Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = [a, a, Path.Combine(dir.Path, "sub", "..", "a.bin")],
            OutputPath = dir["set.par2"],
            SliceSize = 64,
            RecoverySliceCount = 1,
        });
        Assert.AreEqual(2, result.InputSlices);
        Assert.AreEqual(1, Packets(dir["set.par2"]).OfType<Par2FileDescriptionPacket>().Count());
    }

    [TestMethod]
    public void BaseDirectory_DecidesTheStoredNames()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 8, ("data/x/a.bin", 300), ("data/b.bin", 200));
        Directory.CreateDirectory(dir["par"]);
        Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["par/set.par2"], BaseDirectory = dir["data"], SliceSize = 100, RecoverySliceCount = 3 });
        CollectionAssert.AreEquivalent(new[] { "x/a.bin", "b.bin" }, Packets(dir["par/set.par2"]).OfType<Par2FileDescriptionPacket>().Select(d => d.Name).ToArray());

        Assert.IsFalse(Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = dir["par/set.par2"] }).AllFilesComplete, "relative to the .par2 file the names do not exist");
        File.Delete(dir["data/x/a.bin"]);
        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = dir["par/set.par2"], BaseDirectory = dir["data"] });
        Assert.AreEqual(Par2FileStatus.Missing, verify.Files.Single(f => f.File.Name == "x/a.bin").Status);
        Assert.IsTrue(Par2Repairer.Repair(verify).Success);
        TestData.AssertFiles(files);
    }

    [TestMethod]
    public void Create_RejectsInvalidOptions()
    {
        using var dir = new TempDir();
        var a = TestData.WriteFiles(dir, 9, ("a.bin", 1000)).Keys.ToList();
        var outside = Path.Combine(Path.GetTempPath(), "deveparttools-par2-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(outside, [1, 2, 3]);
        try
        {
            Par2CreateOptions Options(IReadOnlyList<string>? inputs = null, long? sliceSize = 100, int? recovery = 1, int first = 0)
                => new() { InputFiles = inputs ?? a, OutputPath = dir["x.par2"], SliceSize = sliceSize, RecoverySliceCount = recovery, FirstExponent = first };

            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(inputs: [])));
            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(inputs: [outside])), "inputs must be below the base directory");
            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(sliceSize: 0)));
            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(sliceSize: 6)));
            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(sliceSize: -4)));
            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(recovery: -1)));
            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(first: -1)));
            Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(Options(recovery: 4, first: 65532)), "exponent 65535 would repeat exponent 0");
            Assert.IsFalse(File.Exists(dir["x.par2"]));

            // The largest allowed exponent is 65534.
            var last = Par2Creator.Create(Options(recovery: 3, first: 65532));
            CollectionAssert.AreEqual(new uint[] { 65532, 65533, 65534 }, last.WrittenFiles.Skip(1).SelectMany(f => Packets(f).OfType<Par2RecoverySlicePacket>()).Select(r => r.Exponent).ToArray());
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    public void Create_RejectsMoreThan32768Slices()
    {
        using var dir = new TempDir();
        var a = TestData.WriteFiles(dir, 10, ("a.bin", 4 * 32768 + 1)).Keys.ToList();
        var e = Assert.ThrowsExactly<ArgumentException>(() => Par2Creator.Create(new Par2CreateOptions { InputFiles = a, OutputPath = dir["x.par2"], SliceSize = 4 }));
        StringAssert.Contains(e.Message, "32769");
        var chosen = Par2Creator.Create(new Par2CreateOptions { InputFiles = a, OutputPath = dir["x.par2"], SliceCount = 40000, RecoverySliceCount = 0 });
        Assert.AreEqual(8, chosen.SliceSize, "the chosen size respects the maximum");
        Assert.AreEqual(16385, chosen.InputSlices);
    }

    [TestMethod]
    public void SliceCount_ChoosesTheSliceSize_AndRedundancyTheRecoveryCount()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 11, ("a.bin", 100_000), ("b.bin", 30_001), ("c.bin", 5));
        var result = Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["set.par2"], SliceCount = 50, RedundancyPercent = 12.5 });
        Assert.AreEqual(Par2Creator.ChooseSliceSize([100_000, 30_001, 5], 50), result.SliceSize);
        Assert.IsTrue(result.InputSlices <= 50);
        Assert.AreEqual((int)Math.Ceiling(result.InputSlices * 0.125), result.RecoverySlices);
    }

    [TestMethod]
    [DataRow(new long[] { 0, 0 }, 10, 4L, DisplayName = "nothing to slice")]
    [DataRow(new long[] { 1000 }, 10, 100L, DisplayName = "exact division")]
    [DataRow(new long[] { 1001 }, 10, 104L, DisplayName = "rounded up to a multiple of 4")]
    [DataRow(new long[] { 10, 10, 10 }, 1, 12L, DisplayName = "fewer slices than files: one slice per file")]
    [DataRow(new long[] { 10, 10, 10 }, -5, 12L, DisplayName = "a target below 1 counts as 1")]
    [DataRow(new long[] { 3 }, 1000, 4L, DisplayName = "at least 4 bytes")]
    [DataRow(new long[] { 0, 0, 10 }, 2, 8L, DisplayName = "empty files need no slices")]
    [DataRow(new long[] { 1L << 40 }, 100_000, 33554432L, DisplayName = "the target is capped at 32768 slices")]
    public void ChooseSliceSize_Examples(long[] lengths, int target, long expected)
        => Assert.AreEqual(expected, Par2Creator.ChooseSliceSize(lengths, target));

    [TestMethod]
    public void ChooseSliceSize_IsTheSmallestSizeThatFits()
    {
        var rnd = new Random(12);
        for (int round = 0; round < 300; round++)
        {
            var lengths = Enumerable.Range(0, rnd.Next(1, 6)).Select(_ => (long)rnd.Next(0, 3000)).ToArray();
            int target = rnd.Next(1, 40);
            long Count(long size) => lengths.Sum(l => (l + size - 1) / size);
            long chosen = Par2Creator.ChooseSliceSize(lengths, target);
            Assert.AreEqual(0, chosen % 4);

            // Brute force: the smallest multiple of 4 that gives at most "target" slices (or one slice per file).
            long best = 4;
            while (Count(best) > target && best < lengths.Max())
            {
                best += 4;
            }

            Assert.AreEqual(best, chosen, $"lengths {string.Join(",", lengths)}, target {target}");
        }
    }

    [TestMethod]
    public void Create_ReportsStagesProgressAndBlockStates()
    {
        using var dir = new TempDir();
        var files = TestData.WriteFiles(dir, 13, ("a.bin", 50_000));
        var observer = new RecordingObserver();
        var ctx = new OperationContext { Observer = observer };
        var result = Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["set.par2"], SliceSize = 5000, RecoverySliceCount = 3 }, ctx);
        CollectionAssert.AreEqual(new[] { "Preparing", "Encoding", "Writing recovery files", "Writing the index file" }, observer.Stages, "one pass: hashed while encoding");
        observer.AssertStagesFollowThePlan();
        Assert.AreEqual(10, ctx.Blocks!.CountStates()[(int)BlockState.Processed]);
        Assert.AreEqual(3, ctx.Blocks.CountRecoveryStates()[(int)RecoveryBlockState.Created]);
        Assert.AreEqual(Path.GetFileName(result.WrittenFiles[1]), ctx.Blocks.RecoveryContainer[0]);
        Assert.AreEqual(files.Keys.Single(), ctx.Blocks.Ownership[9][0].File);
        Assert.AreEqual(45_000, ctx.Blocks.Ownership[9][0].Offset);
    }

    [TestMethod]
    public void Preparing_ManyFiles_ReportsProgress()
    {
        using var dir = new TempDir();
        var inputs = Enumerable.Range(0, 70).Select(i => dir.WriteRandom($"f{i:00}.bin", 100 + i, i)).ToList();
        var observer = new RecordingObserver();
        Par2Creator.Create(new Par2CreateOptions { InputFiles = inputs, OutputPath = dir["set.par2"], RecoverySliceCount = 2 }, new OperationContext { Observer = observer });
        Assert.IsTrue(observer.Fractions.Any(f => f is > 0 and < 1), "preparing 70 files reports on the way");
        observer.AssertStagesFollowThePlan();
    }
}

[TestClass]
public sealed class Par2RecoverySinkTests
{
    [TestMethod]
    public void BuildsTheMd5WhileWriting_OrReadsBackWhenOutOfOrder()
    {
        using var dir = new TestSupport.TempDir();
        const int SliceSize = 1000;
        var data = new byte[SliceSize];
        new Random(3).NextBytes(data);
        byte[] Md5(bool inOrder, out long readBack)
        {
            using var stream = new FileStream(System.IO.Path.Combine(dir.Path, inOrder ? "a.bin" : "b.bin"), FileMode.Create, FileAccess.ReadWrite);
            var header = Enumerable.Range(0, 68).Select(i => (byte)i).ToArray();
            stream.SetLength(header.Length + SliceSize);
            var sink = new Par2Creator.RecoverySink([(stream, header.Length, header)], SliceSize, new DeveParTools.Core.Operations.BlockStateMap(1, 1));
            (int Offset, int Length)[] parts = inOrder ? [(0, 600), (600, 400)] : [(600, 400), (0, 600)];
            foreach (var (offset, length) in parts)
            {
                sink.Write(0, offset, data.AsSpan(offset, length));
            }

            if (!inOrder)
            {
                sink.Write(0, 0, data.AsSpan(0, 600)); // restarted at 0, then again out of order: still read back
                sink.Write(0, 900, data.AsSpan(900, 100));
            }

            readBack = sink.Complete(0);
            Assert.AreEqual(0, sink.Complete(0));
            return header[16..32];
        }

        var built = Md5(true, out long none);
        var read = Md5(false, out long readBytes);
        Assert.AreEqual(0, none);
        Assert.AreEqual(SliceSize, readBytes);
        CollectionAssert.AreEqual(built, read);
        CollectionAssert.AreEqual(System.Security.Cryptography.MD5.HashData(Enumerable.Range(32, 36).Select(i => (byte)i).Concat(data).ToArray()), built);
    }
}

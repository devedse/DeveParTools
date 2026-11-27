using DeveParTools.Core.Hashing;
using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>Creating sets: options, field and code choice, tail packing, dedup, data packets, permissions, volumes.</summary>
[TestClass]
public sealed class CreateTests
{
    private static Par3CreateResult Create(Par3CreateOptions options, OperationContext? context = null) => Par3Creator.Create(options, context);

    private static Par3CreateOptions Options(TempDir dir, ulong? blockSize = 1024, int? recovery = 4, Par3Code code = Par3Code.Cauchy, IReadOnlyList<string>? inputs = null,
        string? output = null, string? baseDir = null, bool recursive = true, int blockCount = 2000, double redundancy = 10, ulong firstRecovery = 0, int? maxRecovery = null,
        Par3Dedup dedup = Par3Dedup.None, bool packTails = true, bool storeData = false, int? volumes = null, string? comment = null, string creator = Par3Creator.DefaultCreator,
        bool unix = false, bool fat = false, bool reproducible = true, int interleave = 0, Par3CauchyConvention convention = Par3CauchyConvention.Par3cmdline,
        RecoveryFileScheme scheme = RecoveryFileScheme.Exponential) => new()
    {
        Scheme = scheme,
        Inputs = inputs ?? [dir.Path],
        OutputPath = output ?? dir["set.par3"],
        BaseDirectory = baseDir,
        Recursive = recursive,
        BlockSize = blockSize,
        BlockCount = blockCount,
        RecoveryBlockCount = recovery,
        RedundancyPercent = redundancy,
        FirstRecoveryBlock = firstRecovery,
        MaxRecoveryBlocks = maxRecovery,
        Code = code,
        Dedup = dedup,
        PackTails = packTails,
        StoreData = storeData,
        VolumeCount = volumes,
        Comment = comment,
        Creator = creator,
        UnixPermissions = unix,
        FatPermissions = fat,
        Reproducible = reproducible,
        Interleave = interleave,
        CauchyConvention = convention,
    };

    /// <summary>Deletes a file, verifies, repairs and checks the content.</summary>
    private static void AssertRepairsDeletedFile(TempDir dir, string relative, string par3 = "set.par3")
    {
        var original = dir.Read(relative);
        File.Delete(dir[relative]);
        var observer = new RecordingObserver();
        var context = new OperationContext { Observer = observer };
        var verify = Par3Files.Verify(dir[par3], context);
        Assert.IsTrue(verify.RepairPossible, $"missing {verify.MissingBlocks}, recovery {verify.RecoveryBlocksAvailable}");
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }, context);
        Assert.IsTrue(repair.Success, repair.Message);
        CollectionAssert.AreEqual(original, dir.Read(relative));
        Assert.IsFalse(observer.Warnings.Any(w => w.Contains("do not match", StringComparison.Ordinal)), string.Join("; ", observer.Warnings));
    }

    [TestMethod]
    public void InvalidOptions_Throw()
    {
        using var dir = new TempDir();
        Assert.ThrowsExactly<ArgumentException>(() => Create(Options(dir)), "an empty directory has no files");
        dir.Write("a.bin", TestData.Random(1, 5000));
        Assert.ThrowsExactly<ArgumentException>(() => Create(Options(dir, blockSize: 1001)));
        Assert.ThrowsExactly<ArgumentException>(() => Create(Options(dir, blockSize: 0)));
        Assert.ThrowsExactly<ArgumentException>(() => Create(Options(dir, recovery: -1)));
        Assert.ThrowsExactly<ArgumentException>(() => Create(Options(dir, recovery: 6, code: Par3Code.Fft)), "FFT needs at least as many inputs as recovery blocks");
        Assert.ThrowsExactly<ArgumentException>(() => Create(Options(dir, code: Par3Code.Fft, maxRecovery: 100)));
        Assert.ThrowsExactly<FileNotFoundException>(() => Create(Options(dir, inputs: [dir["missing.bin"]])));
        using var other = new TempDir();
        var outside = other.Write("x.bin", [1, 2, 3]);
        Assert.ThrowsExactly<ArgumentException>(() => Create(Options(dir, inputs: [outside])), "inputs must be below the base directory");
        Assert.IsFalse(File.Exists(dir["set.par3"]));
    }

    [TestMethod]
    public void ChooseBlockSize_IsAMultipleOf8AndAtLeast64()
    {
        Assert.AreEqual(64UL, Par3Creator.ChooseBlockSize([], 2000, true));
        Assert.AreEqual(64UL, Par3Creator.ChooseBlockSize([1000], 2000, true));
        Assert.AreEqual(504UL, Par3Creator.ChooseBlockSize([1_000_000], 2000, true)); // ceil(500) rounded up to 8: 1,985 blocks
        Assert.AreEqual(1_000_000UL, Par3Creator.ChooseBlockSize([1_000_000], 0, true)); // target clamped to 1
    }

    [TestMethod]
    public void ChooseBlockSize_TheRealCountNeverExceedsTheTarget()
    {
        // Like a TV season: a few large video files and many small subtitle files. Total ÷ target alone gives up to
        // one block per file too many (each file's last block is partly filled).
        var rnd = new Random(5);
        var lengths = Enumerable.Range(0, 5).Select(_ => (long)rnd.Next(400_000_000, 600_000_000))
            .Concat(Enumerable.Range(0, 18).Select(_ => (long)rnd.Next(20_000, 70_000))).ToList();
        foreach (bool pack in new[] { true, false })
        {
            foreach (int target in new[] { 32768, 32767, 32700, 2000, 100, 60 })
            {
                ulong size = Par3Creator.ChooseBlockSize(lengths, target, pack);
                ulong count = Par3Creator.CountBlocks(lengths, size, pack);
                Assert.IsTrue(count <= (ulong)target, $"target {target}, pack {pack}: {count} blocks of {size}");
                Assert.AreEqual(0UL, size % 8);
                Assert.IsTrue(Par3Creator.CountBlocks(lengths, size - 8, pack) > (ulong)target, $"target {target}, pack {pack}: {size} is the smallest that fits");
                Assert.IsTrue(count > (ulong)target * 99 / 100, $"target {target}, pack {pack}: close to the target ({count})");
            }
        }

        // Fewer than 2.5 GB ÷ 64 MiB: the blocks stop at 64 MiB, so there are more than asked for.
        Assert.AreEqual((ulong)(64L << 20), Par3Creator.ChooseBlockSize(lengths, 23, true));

        // Pressing "down" in the block count box: one less always gives fewer real blocks.
        ulong last = Par3Creator.CountBlocks(lengths, Par3Creator.ChooseBlockSize(lengths, 32768, true), true);
        ulong lower = Par3Creator.CountBlocks(lengths, Par3Creator.ChooseBlockSize(lengths, (int)last - 1, true), true);
        Assert.IsTrue(lower < last, $"{last} → {lower}");
    }

    [TestMethod]
    public void ChooseBlockSize_BelowTheFewestBlocks()
    {
        // Without packing every file needs a block of its own: target 1 gives 3 blocks. The smallest size for that is
        // 29,968, not 30,000: the largest file's last 32 bytes are below the 40-byte limit and go inside the packets.
        Assert.AreEqual(29_968UL, Par3Creator.ChooseBlockSize([10_000, 20_000, 30_000], 1, false));
        Assert.AreEqual(3UL, Par3Creator.CountBlocks([10_000, 20_000, 30_000], 29_968, false));
        Assert.AreEqual(4UL, Par3Creator.CountBlocks([10_000, 20_000, 30_000], 29_960, false));

        // With packing all three share one block (60,000 bytes).
        ulong one = Par3Creator.ChooseBlockSize([10_000, 20_000, 30_000], 1, true);
        Assert.AreEqual(60_000UL, one);
        Assert.AreEqual(1UL, Par3Creator.CountBlocks([10_000, 20_000, 30_000], one, true));
    }

    [TestMethod]
    public void FirstFitBlocks_MatchesLookingThroughEveryBlock()
    {
        var rnd = new Random(9);
        const ulong size = 1000;
        var tails = Enumerable.Range(0, 3000).Select(_ => (ulong)rnd.Next(40, 1001)).OrderByDescending(t => t).ToList();
        var naive = new List<ulong>();
        var packed = new FirstFitBlocks(size);
        foreach (var tail in tails)
        {
            int slot = naive.FindIndex(used => size - used >= tail);
            ulong offset = slot < 0 ? 0 : naive[slot];
            if (slot < 0)
            {
                naive.Add(tail);
                slot = naive.Count - 1;
            }
            else
            {
                naive[slot] += tail;
            }

            Assert.AreEqual((slot, offset), packed.Place(tail));
        }

        Assert.AreEqual(naive.Count, packed.Count);
        Assert.IsTrue(packed.Count > 1000, "the tree grew several times");
    }

    [TestMethod]
    public void DefaultBlockSize_FollowsBlockCount()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 100_000));
        var r = Create(Options(dir, blockSize: null, blockCount: 10));
        Assert.AreEqual(10000UL, r.BlockSize);
        Assert.AreEqual(10UL, r.InputBlocks);
    }

    [TestMethod]
    [DataRow(0UL, "Cauchy Reed-Solomon (GF(2^8))", new byte[] { 0x1D })]
    [DataRow(300UL, "Cauchy Reed-Solomon (GF(2^16))", new byte[] { 0x0B, 0x10 })]
    [DataRow(65_530UL, "Cauchy Reed-Solomon (GF(2^32))", new byte[] { 0x8D, 0, 0, 0 })]
    public void Cauchy_FieldGrowsWithBlocksPlusRecoveryIndices(ulong firstRecovery, string description, byte[] generator)
    {
        // x_I and y_R = MAX − R must not collide: blocks + recovery indices ≤ 2^bits − 1.
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 6 * 1024 + 100));
        dir.Write("b.bin", TestData.Random(2, 3000));
        var r = Create(Options(dir, recovery: 3, firstRecovery: firstRecovery));
        Assert.AreEqual(description, r.CodeDescription);
        var set = Par3Files.Load(dir["set.par3"]);
        CollectionAssert.AreEqual(generator, set.Start.GeneratorBytes);
        CollectionAssert.AreEqual(new[] { firstRecovery, firstRecovery + 1, firstRecovery + 2 }, set.RecoveryBlocks.Select(b => b.Index).Order().ToArray());
        Assert.IsTrue(r.WrittenFiles.Any(f => Path.GetFileName(f).StartsWith($"set.vol{firstRecovery:D2}+", StringComparison.Ordinal)), string.Join(", ", r.WrittenFiles));
        AssertRepairsDeletedFile(dir, "b.bin");
    }

    [TestMethod]
    public void Cauchy_ManyBlocksUseGf16()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 300 * 64 + 5));
        var r = Create(Options(dir, blockSize: 64, recovery: 8));
        Assert.AreEqual("Cauchy Reed-Solomon (GF(2^16))", r.CodeDescription);
        TestData.Corrupt(dir["a.bin"], 1000, 400); // bytes 1000..1399: blocks 15..21
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(7, verify.MissingBlocks);
        Assert.IsTrue(Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }).Success);
        Assert.IsTrue(Par3Files.Verify(dir["set.par3"]).AllFilesComplete);
    }

    [TestMethod]
    public void SpecificationConvention_IsRepairedToo()
    {
        // The repairer tries par3cmdline's x_I = I first; the block checksums reject it and x_I = I + 1 is used.
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 4000));
        dir.Write("b.bin", TestData.Random(2, 4000));
        Create(Options(dir, convention: Par3CauchyConvention.Specification));
        AssertRepairsDeletedFile(dir, "a.bin");
    }

    [TestMethod]
    public void Fft_DescriptionsAndCohorts()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 100 * 64));
        dir.Write("b.bin", TestData.Random(2, 150 * 64));
        dir.Write("c.bin", TestData.Random(3, 300));
        Assert.AreEqual("FFT Reed-Solomon (GF(2^8), 1 cohort(s))", Create(Options(dir, blockSize: 64, recovery: 10, code: Par3Code.Fft, inputs: [dir["a.bin"]])).CodeDescription);
        Assert.AreEqual("FFT Reed-Solomon (GF(2^16), 1 cohort(s))", Create(Options(dir, blockSize: 64, recovery: 10, code: Par3Code.Fft, maxRecovery: 100)).CodeDescription);
        Assert.AreEqual("FFT Reed-Solomon (GF(2^8), 3 cohort(s))", Create(Options(dir, blockSize: 64, recovery: 10, code: Par3Code.Fft, interleave: 2)).CodeDescription);

        var set = Par3Files.Load(dir["set.par3"]);
        var fft = (Par3FftMatrixPacket)set.RecoveryBlocks[0].Matrix;
        Assert.AreEqual(2u, fft.Interleave);
        Assert.AreEqual(2, fft.MaxRecoveryLog2, "ceil(10 / 3) = 4 recovery blocks per cohort");
        AssertRepairsDeletedFile(dir, "c.bin");
    }

    [TestMethod]
    public void Fft_MaxRecoveryAboveTheInputCount_MIsLargerThanK()
    {
        // 100 blocks and room for 100 recovery blocks: m = 128 > k = 100. Each cohort is coded with zero blocks
        // appended up to m originals (see Par3Fft remarks).
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 100 * 64));
        Create(Options(dir, blockSize: 64, recovery: 10, code: Par3Code.Fft, maxRecovery: 100));
        Assert.AreEqual(7, ((Par3FftMatrixPacket)Par3Files.Load(dir["set.par3"]).RecoveryBlocks[0].Matrix).MaxRecoveryLog2);
        TestData.Corrupt(dir["a.bin"], 64 * 40, 64 * 10);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(10, verify.MissingBlocks);
        Assert.IsTrue(Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(TestData.Random(1, 100 * 64), dir.Read("a.bin"));
    }

    [TestMethod]
    public void Fft_OneRecoveryBlockIsXor()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 5000));
        dir.Write("b.bin", TestData.Random(2, 100));
        var r = Create(Options(dir, recovery: 1, code: Par3Code.Fft));
        Assert.AreEqual("FFT Reed-Solomon (XOR, 1 cohort(s))", r.CodeDescription);
        Assert.AreEqual(0, Par3Files.Load(dir["set.par3"]).Start.GfSize, "par3cmdline writes field size 0 for XOR");
        AssertRepairsDeletedFile(dir, "b.bin");
    }

    [TestMethod]
    public void Auto_CauchyForSmallSetsFftForLargeOnes()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 300 * 64));
        StringAssert.StartsWith(Create(Options(dir, blockSize: 1024, recovery: 4, code: Par3Code.Auto)).CodeDescription, "Cauchy");
        StringAssert.StartsWith(Create(Options(dir, blockSize: 64, recovery: 30, code: Par3Code.Auto)).CodeDescription, "FFT");
        StringAssert.StartsWith(Create(Options(dir, blockSize: 64, recovery: 400, code: Par3Code.Auto)).CodeDescription, "Cauchy", "more recovery than input blocks");

        // Redundancy in percent when no count is given.
        Assert.AreEqual(30, Create(Options(dir, blockSize: 64, recovery: null, redundancy: 10)).RecoveryBlocks);
    }

    [TestMethod]
    public void ZeroRecoveryBlocks_WritesOnlyTheIndex()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 3000));
        var r = Create(Options(dir, recovery: 0));
        Assert.AreEqual(dir["set.par3"], r.WrittenFiles.Single());
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.IsTrue(verify.AllFilesComplete);
        Assert.AreEqual(0, verify.RecoveryBlocksAvailable);
    }

    [TestMethod]
    public void TailPacking_TailsShareBlocksFirstFitDecreasing()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 1024 + 600));   // tail 600
        dir.Write("b.bin", TestData.Random(2, 1024 + 300));   // tail 300 fits after a's
        dir.Write("c.bin", TestData.Random(3, 500));          // tail 500 opens a second block
        dir.Write("d.bin", TestData.Random(4, 100));          // tail 100 fits in the first block (600+300+100 = 1000)
        dir.Write("tiny.bin", TestData.Random(5, 39));        // inline, no block
        Assert.AreEqual(4UL, Create(Options(dir)).InputBlocks);
        var set = Par3Files.Load(dir["set.par3"]);
        var tails = set.Files.ToDictionary(f => f.RelativePath, f => f.Chunks.Single());
        Assert.AreEqual((2UL, 0UL), (tails["a.bin"].TailBlock, tails["a.bin"].TailOffset));
        Assert.AreEqual((2UL, 600UL), (tails["b.bin"].TailBlock, tails["b.bin"].TailOffset));
        Assert.AreEqual((3UL, 0UL), (tails["c.bin"].TailBlock, tails["c.bin"].TailOffset));
        Assert.AreEqual((2UL, 900UL), (tails["d.bin"].TailBlock, tails["d.bin"].TailOffset));
        CollectionAssert.AreEqual(dir.Read("tiny.bin"), tails["tiny.bin"].InlineTail);

        var map = new OperationContext();
        Par3Files.Verify(dir["set.par3"], map);
        Assert.AreEqual(BlockFlags.TailPacked, map.Blocks!.GetFlags(2));
        Assert.AreEqual(BlockFlags.None, map.Blocks.GetFlags(3));

        Assert.AreEqual(6UL, Create(Options(dir, packTails: false)).InputBlocks);
    }

    [TestMethod]
    public void TailBlocks_ChecksumsCoverZeroPaddingNotThePreviousBlock()
    {
        // Regression: tail blocks were read into a reused buffer that kept the bytes of the previous tail block.
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 1024 + 1000)); // tail block 2, nearly full
        dir.Write("b.bin", TestData.Random(2, 1024 + 100));  // tail block 3, mostly padding
        Create(Options(dir, packTails: false, storeData: true));
        var set = Par3Files.Load(dir["set.par3"]);
        foreach (var f in set.Files)
        {
            var c = f.Chunks.Single();
            var expected = new byte[1024];
            dir.Read(f.RelativePath).AsSpan(1024).CopyTo(expected);
            Assert.AreEqual(new BlockChecksum(Crc64Iso.Compute(expected), Fingerprint.Compute(expected)), set.BlockChecksums[c.TailBlock], f.RelativePath);

            var data = set.DataPackets[c.TailBlock];
            var stored = new byte[data.DataLength];
            using var fs = File.OpenRead(data.SourceFile!);
            fs.Position = data.DataOffset;
            fs.ReadExactly(stored);
            CollectionAssert.AreEqual(expected, stored, "Data packet of " + f.RelativePath);
        }

        // Rebuilding the tail block from recovery data must match its checksum.
        foreach (var p in Directory.GetFiles(dir.Path, "set.part*"))
        {
            File.Delete(p);
        }

        AssertRepairsDeletedFile(dir, "b.bin");
    }

    [TestMethod]
    public void Dedup_IdenticalBlocksAreStoredOnceAndRepairWithoutRecovery()
    {
        using var dir = new TempDir();
        var block = TestData.Random(1, 1024);
        var other = TestData.Random(2, 1024);
        dir.Write("a.bin", [.. block, .. other, .. block, 1, 2, 3]);  // a: X Y X + inline tail
        dir.Write("b.bin", [.. other, .. block]);                       // b: Y X
        var r = Create(Options(dir, recovery: 0, dedup: Par3Dedup.Blocks));
        Assert.AreEqual(2UL, r.InputBlocks);

        var set = Par3Files.Load(dir["set.par3"]);
        var a = set.Files.Single(f => f.RelativePath == "a.bin");
        Assert.AreEqual(2, a.Chunks.Count, "blocks 0 1 0 are two runs");
        Assert.AreEqual((0UL, 2048UL), (a.Chunks[0].FirstBlock, a.Chunks[0].Length));
        Assert.AreEqual((0UL, 1027UL), (a.Chunks[1].FirstBlock, a.Chunks[1].Length));

        var context = new OperationContext();
        Par3Files.Verify(dir["set.par3"], context);
        Assert.AreEqual(BlockFlags.Deduplicated, context.Blocks!.GetFlags(0));

        // With zero recovery blocks, a damaged copy is rebuilt from the intact copy in the other file.
        TestData.Corrupt(dir["b.bin"], 1500);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(0, verify.MissingBlocks);
        Assert.AreEqual(Par3FileStatus.Damaged, verify.Files.Single(f => f.File.RelativePath == "b.bin").Status);
        Assert.IsTrue(Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual((byte[])[.. other, .. block], dir.Read("b.bin"));

        Assert.AreEqual(5UL, Create(Options(dir, recovery: 0)).InputBlocks, "without dedup");
    }

    [TestMethod]
    public void EmptyFilesAndDirectories()
    {
        using var dir = new TempDir();
        dir.Write("empty.bin", []);
        Directory.CreateDirectory(dir["d1/d2"]);
        var r = Create(Options(dir));
        Assert.AreEqual(0UL, r.InputBlocks);
        Assert.AreEqual(0, r.RecoveryBlocks);
        var set = Par3Files.Load(dir["set.par3"]);
        Assert.AreEqual(0UL, set.Files.Single().Length);
        CollectionAssert.AreEquivalent(new[] { "d1", "d1/d2" }, set.Directories.Select(d => d.RelativePath).ToArray());

        File.Delete(dir["empty.bin"]);
        Directory.Delete(dir["d1"], recursive: true);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(2, verify.MissingDirectories.Count);
        Assert.IsTrue(Par3Repairer.Repair(verify).Success);
        Assert.IsTrue(File.Exists(dir["empty.bin"]));
        Assert.IsTrue(Directory.Exists(dir["d1/d2"]));
    }

    [TestMethod]
    public void Inputs_SubdirectoriesRecursionAndSkippedPar3Files()
    {
        using var dir = new TempDir();
        dir.Write("top.bin", [1]);
        dir.Write("sub/a.bin", [2]);
        dir.Write("sub/deep/b.bin", [3]);
        dir.Write("sub/old.par3", [4]);
        Create(Options(dir, inputs: [dir["sub"]]));
        var set = Par3Files.Load(dir["set.par3"]);
        CollectionAssert.AreEquivalent(new[] { "sub/a.bin", "sub/deep/b.bin" }, set.Files.Select(f => f.RelativePath).ToArray());
        CollectionAssert.AreEquivalent(new[] { "sub", "sub/deep" }, set.Directories.Select(d => d.RelativePath).ToArray());

        Create(Options(dir, inputs: [dir["sub"], dir["top.bin"], dir["set.par3"]], recursive: false));
        set = Par3Files.Load(dir["set.par3"]);
        CollectionAssert.AreEquivalent(new[] { "sub/a.bin", "top.bin" }, set.Files.Select(f => f.RelativePath).ToArray());

        // A file named twice is protected once; the base directory can differ from the output directory.
        using var outDir = new TempDir();
        Create(Options(dir, inputs: [dir["top.bin"], dir["top.bin"]], output: outDir["x.par3"], baseDir: dir.Path));
        Assert.AreEqual("top.bin", Par3Files.Load(outDir["x.par3"]).Files.Single().RelativePath);
    }

    [TestMethod]
    public void Reproducible_SameInputGivesIdenticalFiles()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 5000));
        var first = Create(Options(dir));
        var bytes = first.WrittenFiles.Select(File.ReadAllBytes).ToList();
        var second = Create(Options(dir));
        Assert.AreEqual(first.SetId, second.SetId);
        for (int i = 0; i < bytes.Count; i++)
        {
            CollectionAssert.AreEqual(bytes[i], File.ReadAllBytes(second.WrittenFiles[i]));
        }

        dir.Write("a.bin", TestData.Random(2, 5000));
        Assert.AreNotEqual(first.SetId, Create(Options(dir)).SetId);
        Assert.AreNotEqual(Create(Options(dir, reproducible: false)).SetId, Create(Options(dir, reproducible: false)).SetId);
    }

    [TestMethod]
    public void CommentAndCreator()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", [1, 2, 3]);
        Create(Options(dir, comment: "Holiday photos", creator: "My tool 1.0"));
        var set = Par3Files.Load(dir["set.par3"]);
        Assert.AreEqual("Holiday photos", set.Comments.Single());
        Assert.AreEqual("My tool 1.0", set.Creators.Single());

        Create(Options(dir));
        set = Par3Files.Load(dir["set.par3"]);
        Assert.AreEqual(0, set.Comments.Count);
        Assert.AreEqual(Par3Creator.DefaultCreator, set.Creators.Single());
    }

    [TestMethod]
    public void Permissions_PacketsForFilesAndDirectories()
    {
        using var dir = new TempDir();
        var file = dir.Write("sub/a.bin", [1, 2, 3]);
        var mtime = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddTicks(1234567);
        File.SetLastWriteTimeUtc(file, mtime);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.SetUser);
        }

        Create(Options(dir, unix: true, fat: true));
        var set = Par3Files.Load(dir["set.par3"]);
        var opts = set.Files.Single().Options;
        var unix = opts.OfType<Par3UnixPermissionsPacket>().Single();
        var fat = opts.OfType<Par3FatPermissionsPacket>().Single();
        Assert.AreEqual((mtime - DateTime.UnixEpoch).Ticks * 100, unix.Mtime, "nanoseconds since 1970");
        Assert.AreEqual(Par3UnixPermissionsPacket.UnsetTime, unix.Atime);
        Assert.AreEqual(Par3UnixPermissionsPacket.UnsetId, unix.Uid);
        Assert.AreEqual(OperatingSystem.IsWindows() ? 0x1A4 : 0x9A0, unix.Mode, "0o4640: the 12 mode bits are stored as they are");
        Assert.AreEqual((ulong)mtime.ToFileTimeUtc(), fat.LastWrite, "FILETIME");
        Assert.AreEqual(Par3FatPermissionsPacket.UnsetTime, fat.LastAccess);
        Assert.AreEqual(2, set.Directories.Single().Options.Count);

        // Identical option packets are stored once and shared.
        dir.Write("sub/b.bin", [4]);
        File.SetLastWriteTimeUtc(dir["sub/b.bin"], mtime);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir["sub/b.bin"], File.GetUnixFileMode(file));
        }

        Create(Options(dir, unix: true));
        var packets = Par3Files.Packets(dir["set.par3"]);
        Assert.AreEqual(2, packets.OfType<Par3UnixPermissionsPacket>().Count(), "one for both files, one for the directory");
    }

    [TestMethod]
    public void Volumes_ExponentialOrFixedCount()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 20_000));
        var r = Create(Options(dir, recovery: 10));
        CollectionAssert.AreEqual(new[] { "set.par3", "set.vol00+01.par3", "set.vol01+02.par3", "set.vol03+04.par3", "set.vol07+03.par3" }, r.WrittenFiles.Select(Path.GetFileName).ToArray());

        // 3 files, exponential (as par2cmdline -n): low = 2 is the smallest power of two with low·(1 + 2 + 4) ≥ 10.
        r = Create(Options(dir, recovery: 10, volumes: 3, firstRecovery: 95));
        CollectionAssert.AreEqual(new[] { "set.par3", "set.vol095+02.par3", "set.vol097+04.par3", "set.vol101+04.par3" }, r.WrittenFiles.Select(Path.GetFileName).ToArray());

        // Equal files, and files no larger than the largest input (20,000 bytes = 19 blocks of 1024: no limit here).
        r = Create(Options(dir, recovery: 10, volumes: 3, firstRecovery: 95, scheme: RecoveryFileScheme.Uniform));
        CollectionAssert.AreEqual(new[] { "set.par3", "set.vol095+04.par3", "set.vol099+03.par3", "set.vol102+03.par3" }, r.WrittenFiles.Select(Path.GetFileName).ToArray());
        r = Create(Options(dir, blockSize: 8192, recovery: 5, scheme: RecoveryFileScheme.LimitSize));
        CollectionAssert.AreEqual(new[] { "set.par3", "set.vol00+01.par3", "set.vol01+02.par3", "set.vol03+02.par3" }, r.WrittenFiles.Select(Path.GetFileName).ToArray(), "at most 2 blocks (20,000 / 8192) per file");

        // Every volume has the vital packets: each one alone is enough to verify.
        foreach (var v in r.WrittenFiles.Skip(1))
        {
            var only = new TempDir();
            using (only)
            {
                File.Copy(v, only[Path.GetFileName(v)]);
                File.Copy(dir["a.bin"], only["a.bin"]);
                Assert.IsTrue(Par3Files.Verify(only[Path.GetFileName(v)]).AllFilesComplete, v);
            }
        }
    }

    [TestMethod]
    public void PlanVolumes_Naming()
    {
        static string[] Plan(int count, int? volumes = null, ulong first = 0, string marker = ".vol", RecoveryFileScheme scheme = RecoveryFileScheme.Exponential, long limit = long.MaxValue)
            => Par3Creator.PlanVolumes("/x/set.par3", scheme, volumes, first, count, marker, limit)
                .Select(v => $"{Path.GetFileName(v.Path)}:{v.First}:{v.Count}").ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), Plan(0));
        CollectionAssert.AreEqual(Array.Empty<string>(), Plan(0, 3));
        CollectionAssert.AreEqual(new[] { "set.vol00+01.par3:0:1" }, Plan(1));
        CollectionAssert.AreEqual(new[] { "set.vol00+01.par3:0:1", "set.vol01+02.par3:1:2", "set.vol03+02.par3:3:2" }, Plan(5));
        CollectionAssert.AreEqual(new[] { "set.vol00+01.par3:0:1", "set.vol01+02.par3:1:2", "set.vol03+02.par3:3:2" }, Plan(5, 3), "exponential with 3 files: 1, 2, 4 capacity");
        CollectionAssert.AreEqual(new[] { "set.vol00+02.par3:0:2", "set.vol02+02.par3:2:2", "set.vol04+01.par3:4:1" }, Plan(5, 3, scheme: RecoveryFileScheme.Uniform));
        CollectionAssert.AreEqual(new[] { "set.vol00+01.par3:0:1", "set.vol01+02.par3:1:2", "set.vol03+02.par3:3:2", "set.vol05+01.par3:5:1" }, Plan(6, 3, scheme: RecoveryFileScheme.LimitSize, limit: 2), "limit: at most 2 per file, the count is not used");
        CollectionAssert.AreEqual(new[] { "set.vol00+01.par3:0:1", "set.vol01+01.par3:1:1" }, Plan(2, 10), "at most one volume per block");
        Assert.AreEqual("set.part0998+03.par3:0:3", Plan(3, 1, 998, ".part", RecoveryFileScheme.Uniform).Single());
        Assert.AreEqual("set.vol0998+03.par3:0:3", Plan(3, 1, 998).Single());
        Assert.AreEqual("noext.vol00+01.par3:0:1", Par3Creator.PlanVolumes("/x/noext", RecoveryFileScheme.Exponential, null, 0, 1, ".vol", long.MaxValue).Select(v => $"{Path.GetFileName(v.Path)}:{v.First}:{v.Count}").Single());
    }

    [TestMethod]
    [DataRow(3, 0.2, 10)]
    [DataRow(10, 0.5, 24)]
    [DataRow(1, 0.0, 3)]
    [DataRow(1000, 0.9, 30)]
    public void VitalCopies_SpecAppendixD(int packets, double failureRate, int expected)
    {
        // n = (ln(1e-6) − ln(packets)) / ln(f), f clamped to [0.01, 0.5].
        Assert.AreEqual(expected, Par3Creator.VitalCopies(packets, failureRate));
    }

    [TestMethod]
    public void ArchiveMode_PartsWithDataPackets()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.bin", TestData.Random(1, 5 * 1024));
        var r = Create(Options(dir, recovery: 1, storeData: true, volumes: 2));
        CollectionAssert.AreEqual(new[] { "set.par3", "set.vol00+01.par3", "set.part00+03.par3", "set.part03+02.par3" }, r.WrittenFiles.Select(Path.GetFileName).ToArray());
        File.Delete(a);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(0, verify.MissingBlocks);
        Assert.IsTrue(Par3Repairer.Repair(verify).Success);
        CollectionAssert.AreEqual(TestData.Random(1, 5 * 1024), File.ReadAllBytes(a));
    }

    [TestMethod]
    [DataRow(Par3Code.Cauchy)]
    [DataRow(Par3Code.Fft)]
    public void SmallMemoryBudget_RecoveryIsWrittenInSlices(Par3Code code)
    {
        // With 4 KiB of memory a 64 KiB block is encoded in slices, so each packet checksum is computed from disk.
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 8 * 65536 + 7000));
        var observer = new RecordingObserver();
        var context = new OperationContext { MemoryBudget = 4096, Observer = observer, Threads = 2 };
        var r = Create(Options(dir, blockSize: 65536, recovery: 3, code: code), context);
        Assert.AreEqual(9UL, r.InputBlocks);
        CollectionAssert.IsSubsetOf(new[] { "Preparing", "Hashing", "Writing metadata", "Encoding" }, observer.Stages);
        Assert.AreEqual(1.0, observer.LastProgress);
        Assert.IsTrue(Enumerable.Range(0, 3).All(i => context.Blocks!.GetRecovery(i) == RecoveryBlockState.Created));
        Assert.IsTrue(Enumerable.Range(0, 9).All(i => context.Blocks!.Get(i) == BlockState.Processed));

        var set = Par3Files.Load(dir["set.par3"]);
        Assert.AreEqual(3, set.RecoveryBlocks.Count, "every recovery packet has a valid checksum");
        TestData.Corrupt(dir["a.bin"], 100_000, 10);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.IsTrue(Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }, new OperationContext { MemoryBudget = 4096 }).Success);
        Assert.IsTrue(Par3Files.Verify(dir["set.par3"]).AllFilesComplete);
    }

    [TestMethod]
    public void ManyBlocks_ExternalDataPacketsHoldAtMost1024Checksums()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 2500 * 8));
        Create(Options(dir, blockSize: 8, recovery: 2));
        var ext = Par3Files.Packets(dir["set.par3"]).OfType<Par3ExternalDataPacket>().ToList();
        CollectionAssert.AreEqual(new ulong[] { 0, 1024, 2048 }, ext.Select(e => e.FirstBlockIndex).ToArray());
        CollectionAssert.AreEqual(new[] { 1024, 1024, 452 }, ext.Select(e => e.Checksums.Count).ToArray());
    }
}

/// <summary>Blocks larger than a hashing piece (1 MiB), the block size limit, and Cancel during hashing.</summary>
[TestClass]
public sealed class LargeBlockTests
{
    [TestMethod]
    public void BlocksLargerThanAHashingPiece_VerifyAndRepair()
    {
        using var dir = new TempDir();
        // 3 MiB blocks: whole blocks, a tail of 1.5 MiB, and two files that are each smaller than a block, so their
        // tails share one packed block, all hashed in pieces.
        var big = TestData.Random(51, 3 * (3 << 20) + (3 << 19));
        var small1 = TestData.Random(52, (1 << 20) + 12345);
        var small2 = TestData.Random(53, (1 << 20) + 777);
        var paths = new[] { dir.Write("big.bin", big), dir.Write("s1.bin", small1), dir.Write("s2.bin", small2) };
        Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 3 << 20, RecoveryBlockCount = 3, Code = Par3Code.Cauchy, StoreData = true });
        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["set.par3"] });
        Assert.IsTrue(verify.AllFilesComplete, "the checksums built in pieces match the files");

        // Without the originals: rebuilt from the stored data (also written in pieces).
        foreach (var p in paths)
        {
            File.Delete(p);
        }

        var report = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["set.par3"] });
        Assert.IsTrue(Par3Repairer.Repair(report, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(big, dir.Read("big.bin"));
        CollectionAssert.AreEqual(small1, dir.Read("s1.bin"));
        CollectionAssert.AreEqual(small2, dir.Read("s2.bin"));
    }

    [TestMethod]
    public void BlockSizeLimit_ChoosingStopsThere_AndLargerIsRefused()
    {
        long[] lengths = [200L << 20, 50L << 20];
        ulong size = Par3Creator.ChooseBlockSize(lengths, 1, true);
        Assert.AreEqual((ulong)(64L << 20), size, "one block was asked for; 64 MiB is the most");
        Assert.AreEqual(4UL, Par3Creator.CountBlocks(lengths, size, true), "3 full blocks of the first file; both tails (8 and 50 MiB) share one");

        using var dir = new TempDir();
        var path = dir.Write("a.bin", TestData.Random(5, 100_000));
        var e = Assert.ThrowsExactly<ArgumentException>(() => Par3Creator.Create(new Par3CreateOptions { Inputs = [path], OutputPath = dir["set.par3"], BlockSize = 128UL << 20 }));
        StringAssert.Contains(e.Message, "64 MiB");
    }

    [TestMethod]
    public void Cancel_DuringHashing_StopsWithinAPiece()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.bin", TestData.Random(6, 12 << 20));
        using var cancel = new CancellationTokenSource();
        var observer = new CancellingObserver(cancel);
        var context = new OperationContext { Observer = observer, CancellationToken = cancel.Token, Threads = 1 };
        Assert.ThrowsExactly<OperationCanceledException>(() => Par3Creator.Create(new Par3CreateOptions { Inputs = [path], OutputPath = dir["set.par3"], BlockSize = 12 << 20 }, context));
        Assert.IsTrue(observer.HashingReports is > 0 and < 12, $"one 12 MiB block, read in 1 MiB pieces: stopped after {observer.HashingReports}");
        StringAssert.EndsWith(observer.LastDetail, "MiB read");
    }

    private sealed class CancellingObserver(CancellationTokenSource cancel) : IParOperationObserver
    {
        private string _stage = string.Empty;

        public int HashingReports { get; private set; }

        public string LastDetail { get; private set; } = string.Empty;

        public void Stage(string name) => _stage = name;

        public void Progress(double fraction, string? detail = null)
        {
            if (_stage == Par3Stages.Hashing)
            {
                HashingReports++;
                LastDetail = detail ?? string.Empty;
                cancel.Cancel();
            }
        }

        public void Log(ParLogLevel level, string message)
        {
        }
    }
}

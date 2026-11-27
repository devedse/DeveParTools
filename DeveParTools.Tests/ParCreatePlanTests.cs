using DeveParTools.Core.Operations;
using DeveParTools.Par3;

namespace DeveParTools.Tests;

/// <summary><see cref="ParClient.Plan"/> must predict exactly what <see cref="ParClient.Create"/> makes.</summary>
[TestClass]
public sealed class ParCreatePlanTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "deveparttools-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, int length, int seed = 1)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[length];
        new Random(seed + length).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Creates with the plan's exact numbers and compares block size, counts and recovery file names.</summary>
    private void AssertPlanMatchesCreate(ParCreateRequest request)
    {
        var plan = ParClient.Plan(request);
        Assert.IsEmpty(plan.Warnings.Where(w => !w.StartsWith("Store data", StringComparison.Ordinal) && !w.StartsWith("Only ", StringComparison.Ordinal)).Concat(plan.Errors), string.Join("; ", plan.Warnings.Concat(plan.Errors)));
        var report = ParClient.Create(request);

        // Every file that was written, with exactly the planned size.
        Assert.IsNotNull(plan.OutputFiles);
        CollectionAssert.AreEquivalent(report.WrittenFiles.Select(Path.GetFullPath).ToList(), plan.OutputFiles.Select(f => Path.GetFullPath(f.Path)).ToList(), "the files");
        foreach (var f in plan.OutputFiles)
        {
            Assert.AreEqual(new FileInfo(f.Path).Length, f.Bytes, Path.GetFileName(f.Path));
        }

        Assert.AreEqual(plan.RecoveryBlocks, plan.OutputFiles.Sum(f => f.RecoveryBlocks));
        Assert.IsTrue(plan.Efficiency is > 0 and <= 1, $"efficiency {plan.Efficiency}");
        Assert.AreEqual(plan.BlockSize, report.BlockSize, "block size");
        Assert.AreEqual(plan.InputBlocks, report.InputBlocks, "input blocks");
        Assert.AreEqual(plan.RecoveryBlocks, report.RecoveryBlocks, "recovery blocks");
        var written = report.WrittenFiles.Select(Path.GetFileName).ToHashSet();
        foreach (var f in plan.RecoveryFiles)
        {
            Assert.Contains(Path.GetFileName(f.Path), written, $"{f.Path} in {string.Join(", ", written)}");
        }

        Assert.AreEqual(plan.RecoveryFiles.Sum(f => f.Count), plan.RecoveryBlocks);
        if (plan.Format == ParFormat.Par3)
        {
            Assert.AreEqual(plan.Code, report.Description);
        }

        foreach (var f in report.WrittenFiles)
        {
            File.Delete(f);
        }
    }

    [TestMethod]
    [DataRow(ParFormat.Par2, 2000, 10.0, false, false, null)]
    [DataRow(ParFormat.Par2, 37, 33.0, true, false, 3)]
    [DataRow(ParFormat.Par2, 500, 5.0, false, true, null)]
    [DataRow(ParFormat.Par2, 1, 100.0, false, false, 2)]
    [DataRow(ParFormat.Par3, 2000, 10.0, false, false, null)]
    [DataRow(ParFormat.Par3, 300, 12.5, true, false, 4)]
    [DataRow(ParFormat.Par3, 64, 50.0, false, true, null)]
    [DataRow(ParFormat.Par3, 5000, 1.0, false, false, 2)]
    public void Plan_MatchesCreate(ParFormat format, int blockCount, double redundancy, bool uniform, bool limit, int? volumes)
    {
        string[] inputs =
        [
            Write("big.bin", 300_000),
            Write("medium.bin", 77_777),
            Write("dir/small.bin", 1_234),
            Write("dir/tiny.bin", 41),
            Write("dir/sub/other.bin", 20_000),
        ];
        var extension = format == ParFormat.Par2 ? ".par2" : ".par3";
        AssertPlanMatchesCreate(new ParCreateRequest
        {
            Inputs = format == ParFormat.Par3 ? [inputs[0], inputs[1], Path.Combine(_dir, "dir")] : inputs,
            OutputPath = Path.Combine(_dir, "set" + extension),
            BlockCount = blockCount,
            RedundancyPercent = redundancy,
            UniformVolumes = uniform,
            LimitVolumeSize = limit,
            VolumeCount = volumes,
        });
    }

    [TestMethod]
    public void Plan_MatchesCreate_Par3TailPackingCodesAndExplicitNumbers()
    {
        var files = Enumerable.Range(0, 12).Select(i => Write($"f{i}.bin", 1000 + i * 357, i)).ToArray();
        foreach (var pack in new[] { true, false })
        {
            AssertPlanMatchesCreate(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "a.par3"), BlockSize = 1024, Par3PackTails = pack, RecoveryBlockCount = 5 });
        }

        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "b.par3"), BlockCount = 400, Par3Code = Par3Code.Fft, RedundancyPercent = 20, FirstRecoveryBlock = 3, Par3MaxRecoveryBlocks = 256 });
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "c.par3"), BlockCount = 400, Par3Code = Par3Code.Cauchy, RedundancyPercent = 20, Par3Interleave = 0 });
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "d.par3"), BlockCount = 2000, Par3Code = Par3Code.Fft, RecoveryBlockCount = 40, Par3Interleave = 1 });
    }

    [TestMethod]
    public void Plan_FileSizes_MatchCreate_WithMetadataOptions()
    {
        var files = new[] { Write("x/a.bin", 50_000), Write("x/b.bin", 33), Write("x/sub/c.bin", 7_777), Write("x/sub/deeper/d.bin", 100) };
        var tree = Path.Combine(_dir, "x");
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = [tree], OutputPath = Path.Combine(_dir, "m1.par3"), BlockSize = 512, Comment = "Ünïcode comment", Par3UnixPermissions = true, Par3FatPermissions = true, VolumeCount = 3 });
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = [tree], OutputPath = Path.Combine(_dir, "m2.par3"), BlockSize = 1024, Par3StoreData = true, Par3PackTails = false, RedundancyPercent = 50 });
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = [tree], OutputPath = Path.Combine(_dir, "m3.par3"), BlockSize = 1024, Par3StoreData = true, VolumeCount = 2 });
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "m4.par2"), BlockCount = 50, Comment = "a comment", RecoveryBlockCount = 40, VolumeCount = 3, UniformVolumes = true });
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = [.. files, Write("x/empty.bin", 0)], OutputPath = Path.Combine(_dir, "m5.par2"), BlockCount = 50 });
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = [Write("p1.bin", 100), Write("p2.bin", 2_000)], OutputPath = Path.Combine(_dir, "m6.par"), RecoveryBlockCount = 2, Comment = "PAR1 comment" });
    }

    [TestMethod]
    public void Plan_Efficiency_CountsTheOverheadOfSmallBlocks()
    {
        var files = Enumerable.Range(0, 4).Select(i => Write($"e{i}.bin", 200_000 + i, i)).ToArray();
        double Efficiency(long blockSize) => ParClient.Plan(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "e.par3"), BlockSize = blockSize, RedundancyPercent = 100 }).Efficiency!.Value;

        // 216-byte blocks: each block costs a 24-byte checksum in every copy of the set description, and 88 bytes of
        // packet header as a recovery block, so most of what is written is overhead.
        double tiny = Efficiency(216), large = Efficiency(16384);
        Assert.IsTrue(tiny < 0.5, $"tiny blocks {tiny:P1}");
        StringAssert.Contains(string.Join(" ", ParClient.Plan(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "e.par3"), BlockSize = 216, RedundancyPercent = 100 }).Warnings), "of what is written is recovery data");
        Assert.IsTrue(large > 0.9, $"large blocks {large:P1}");
        Assert.IsNull(ParClient.Plan(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "e.par3") }, withFileSizes: false).OutputFiles, "sizes only when asked");
        Assert.IsNull(ParClient.Plan(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "e.par3"), BlockSize = 12 }).OutputFiles, "no sizes when Create would refuse");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;
        var outside = ParClient.Plan(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "e.par2"), BaseDirectory = elsewhere });
        Assert.IsTrue(outside.OutputFiles == null && outside.InputBlocks > 0, "inputs outside the base folder: Create would refuse, so no sizes, but still a block plan");
        var scanned = ParClient.ScanInputs(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "e.par3") });
        File.Delete(files[0]);
        Assert.IsNull(ParClient.Plan(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "e.par2") }, scanned).OutputFiles, "an input that disappeared: no sizes, but still a block plan");
    }

    [TestMethod]
    public void Plan_MatchesCreate_Par1()
    {
        var files = new[] { Write("a.bin", 5000), Write("b.bin", 7000), Write("c.bin", 100) };
        var request = new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "set.par"), RedundancyPercent = 50 };
        var plan = ParClient.Plan(request);
        Assert.AreEqual(7000, plan.BlockSize);
        Assert.AreEqual(3, plan.InputBlocks);
        Assert.AreEqual(3, plan.MaxInputBlocks);
        Assert.AreEqual(2, plan.RecoveryBlocks, "ceil(3 × 50%)");
        CollectionAssert.AreEqual(new[] { "set.p01", "set.p02" }, plan.RecoveryFiles.Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.IsTrue(plan.Files.All(f => f.Blocks == 1));
        AssertPlanMatchesCreate(request);
    }

    [TestMethod]
    public void Plan_DocumentedPar2Example()
    {
        var request = new ParCreateRequest { Inputs = [Write("a.bin", 1_000_000), Write("b.bin", 10_000)], OutputPath = Path.Combine(_dir, "set.par2"), BlockCount = 100 };
        var plan = ParClient.Plan(request);
        Assert.AreEqual(ParFormat.Par2, plan.Format);
        Assert.AreEqual(10_104, plan.BlockSize);
        Assert.AreEqual(100, plan.InputBlocks);
        Assert.AreEqual(10, plan.RecoveryBlocks);
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 3 }, plan.RecoveryFiles.Select(f => f.Count).ToArray());
        Assert.AreEqual(1_010_000 / (100 * 10_104.0), plan.BlockFill, 1e-9);
        Assert.AreEqual(10.0, plan.RedundancyPercent, 1e-9);
        Assert.AreEqual(101_040, plan.RecoveryBytes);
        Assert.AreEqual(1_010_000, plan.TotalBytes);
        Assert.AreEqual(1_000_000, plan.LargestFile);
        Assert.AreEqual("Reed-Solomon (GF(2^16))", plan.Code);
        Assert.AreEqual(32768, plan.MaxInputBlocks);
        Assert.AreEqual((0, 1), plan.RebuildableFiles, "a.bin (99 blocks) never fits in 10 recovery blocks; b.bin (1 block) does");
    }

    [TestMethod]
    public void RebuildableFiles_MinTakesTheLargestFirst_MaxTheSmallest()
    {
        var plan = new ParCreatePlan
        {
            Format = ParFormat.Par3,
            Files = [new("a", 0, "a", 0), new("b", 100, "b", 1), new("c", 300, "c", 3), new("d", 500, "d", 5), new("e", 30, "e", 0)],
            BlockSize = 100,
            InputBlocks = 9,
            RecoveryBlocks = 6,
            RecoveryFiles = [],
            Code = "x",
            MaxInputBlocks = 9,
        };

        // Largest first: d (5) fits, c (3) does not → 1 + 2 free files. Smallest first: b (1) + c (3) fit, d does not → 2 + 2.
        Assert.AreEqual((3, 4), plan.RebuildableFiles);
        Assert.AreEqual(930.0 / 900, plan.BlockFill, 1e-9);
        var empty = new ParCreatePlan { Format = ParFormat.Par2, Files = [], BlockSize = 4, InputBlocks = 0, RecoveryBlocks = 0, RecoveryFiles = [], Code = "x", MaxInputBlocks = 0 };
        Assert.AreEqual(0, empty.BlockFill);
        Assert.IsNull(empty.Efficiency, "no sizes were planned");
        Assert.IsNull(empty.ParFileBytes);
        Assert.AreEqual(0, empty.RedundancyPercent);
        Assert.AreEqual(0, empty.LargestFile);
        Assert.AreEqual((0, 0), empty.RebuildableFiles);
    }

    [TestMethod]
    public void BlocksAreAtMost64MiB_TheSliderStartsAtTheFewestBlocks()
    {
        // Only the lengths matter for a plan: a 200 MiB file without data.
        var big = Path.Combine(_dir, "disk.vhdx");
        using (var fs = new FileStream(big, FileMode.Create))
        {
            fs.SetLength(200L << 20);
        }

        foreach (var output in new[] { "d.par3", "d.par2" })
        {
            var asked = ParClient.Plan(new ParCreateRequest { Inputs = [big], OutputPath = Path.Combine(_dir, output), BlockCount = 1 }, withFileSizes: false);
            Assert.AreEqual(64L << 20, asked.BlockSize, output);
            Assert.AreEqual(4, asked.InputBlocks, output + ": 200 MiB in blocks of at most 64 MiB");
            Assert.AreEqual(4, asked.MinInputBlocks, output);
            Assert.IsTrue(asked.Errors.Count == 0 && asked.Warnings.Any(w => w.StartsWith("Blocks are at most 64 MiB", StringComparison.Ordinal)), output + ": " + string.Join(" | ", asked.Warnings));

            var typed = ParClient.Plan(new ParCreateRequest { Inputs = [big], OutputPath = Path.Combine(_dir, output), BlockSize = 128L << 20 }, withFileSizes: false);
            StringAssert.Contains(string.Join(" | ", typed.Errors), "at most 67,108,864 bytes", output);
        }

        var normal = ParClient.Plan(new ParCreateRequest { Inputs = [big], OutputPath = Path.Combine(_dir, "n.par3") }, withFileSizes: false);
        Assert.IsFalse(normal.Warnings.Any(w => w.StartsWith("Blocks are at most", StringComparison.Ordinal)), "2,000 blocks of 100 KiB: nothing to say");
        Assert.AreEqual(4, normal.MinInputBlocks);
        var sized = ParClient.Plan(new ParCreateRequest { Inputs = [Write("x.bin", 1000)], OutputPath = Path.Combine(_dir, "x.par3"), BlockCount = 1 });
        Assert.AreEqual(1, sized.MinInputBlocks, "kept when the sizes are added");
    }

    [TestMethod]
    public void FewHugeBlocks_EveryFileCanBeRebuiltAndThereIsAWarning()
    {
        // 23 files packed into 1 block (PAR3 tail packing), 1 recovery block: that block brings back all 23 files.
        var files = Enumerable.Range(0, 23).Select(i => Write($"season/e{i:00}.srt", 3000 + i * 100)).ToList();
        var plan = ParClient.Plan(new ParCreateRequest { Inputs = [Path.Combine(_dir, "season")], OutputPath = Path.Combine(_dir, "one.par3"), BlockCount = 1, RecoveryBlockCount = 1 });
        Assert.AreEqual(1, plan.InputBlocks);
        Assert.AreEqual(23, plan.Files.Count);
        Assert.AreEqual((23, 23), plan.RebuildableFiles);

        // A few blocks of a megabyte or more: one damaged byte costs a whole block.
        var big = Write("big.bin", 3 << 20);
        foreach (var output in new[] { "big.par2", "big.par3" })
        {
            var few = ParClient.Plan(new ParCreateRequest { Inputs = [big], OutputPath = Path.Combine(_dir, output), BlockCount = 3 });
            Assert.AreEqual(3, few.InputBlocks, output);
            StringAssert.Contains(string.Join(" | ", few.Warnings), "Only 3 block(s) of 1 MiB: any damage, even a single byte", output);
        }

        Assert.IsFalse(ParClient.Plan(new ParCreateRequest { Inputs = [big], OutputPath = Path.Combine(_dir, "big.par3") }).Warnings.Any(w => w.Contains("any damage", StringComparison.Ordinal)), "2,000 blocks by default: no warning");
        AssertPlanMatchesCreate(new ParCreateRequest { Inputs = [Path.Combine(_dir, "season")], OutputPath = Path.Combine(_dir, "one-real.par3"), BlockCount = 1, RecoveryBlockCount = 1 });
    }

    [TestMethod]
    public void ScanInputs_ExpandsFolders_LikeCreatePerFormat()
    {
        Write("tree/a.bin", 10);
        Write("tree/old.par2", 10);
        Write("tree/old.par3", 10);
        Write("tree/sub/b.bin", 20);
        var single = Write("single.bin", 30);
        var output = Path.Combine(_dir, "tree", "set.par3");

        string[] Names(ParFormat format, bool recursive, string outputPath) => ParClient.ScanInputs(new ParCreateRequest
        {
            Inputs = [Path.Combine(_dir, "tree"), single, single, Path.Combine(_dir, "missing.bin")],
            OutputPath = outputPath,
            Format = format,
            Recursive = recursive,
        }).Select(f => Path.GetRelativePath(_dir, f.Path).Replace('\\', '/')).ToArray();

        CollectionAssert.AreEqual(new[] { "tree/a.bin", "tree/sub/b.bin", "single.bin" }, Names(ParFormat.Par2, true, output), "PAR2 leaves PAR files out of folders");
        CollectionAssert.AreEqual(new[] { "tree/a.bin", "tree/old.par2", "tree/sub/b.bin", "single.bin" }, Names(ParFormat.Par3, true, output), "PAR3 leaves only .par3 files out");
        CollectionAssert.AreEqual(new[] { "tree/a.bin", "tree/old.par2", "single.bin" }, Names(ParFormat.Par3, false, output));
        File.WriteAllBytes(output, [1, 2, 3]);
        CollectionAssert.AreEqual(new[] { "tree/old.par2", "single.bin" }, Names(ParFormat.Par3, false, Path.Combine(_dir, "tree", "a.bin")), "the output itself is not an input");

        var scanned = ParClient.ScanInputs(new ParCreateRequest { Inputs = [single], OutputPath = Path.Combine(_dir, "x.par2") });
        Assert.AreEqual(new ParPlanFile(single, 30, single, 0), scanned.Single());
        Assert.AreEqual(8, ParClient.Plan(new ParCreateRequest { Inputs = [single], OutputPath = Path.Combine(_dir, "x.par2") }, scanned).InputBlocks, "a scanned list can be passed in: 30 bytes in blocks of 4");
    }

    [TestMethod]
    public void Plan_Warnings()
    {
        var a = Write("a.bin", 600_000); // 150,000 PAR2 blocks of 4 bytes, 75,000 PAR3 blocks of 8 bytes
        var other = Write("elsewhere/b.bin", 10);
        string Only(ParCreateRequest r) => string.Join(" | ", ParClient.Plan(r).Errors.Concat(ParClient.Plan(r).Warnings));

        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par2"), BlockSize = 6 }), "multiple of 4");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par2"), BlockSize = 0 }), "multiple of 4");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par2"), BlockSize = 4 }), "at most 32,768 input blocks");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par2"), FirstRecoveryBlock = 65530, RecoveryBlockCount = 10 }), "below 65,535");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par3"), BlockSize = 12 }), "multiple of 8");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par3"), BlockCount = 10, Par3Code = Par3Code.Fft, RecoveryBlockCount = 20 }), "FFT code needs");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par3"), BlockSize = 8, Par3Code = Par3Code.Cauchy, RecoveryBlockCount = 1 }), "GF(2^32)");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par3"), Par3Dedup = Par3Dedup.Blocks }), "Deduplication stores blocks");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par3"), Par3StoreData = true }), "full second copy of your files (585.94 KiB)");
        Assert.AreEqual("100 bytes", ParClient.FormatBytes(100));
        Assert.AreEqual("1.5 KiB", ParClient.FormatBytes(1536));
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a, other], OutputPath = Path.Combine(_dir, "s.par") }), "bare file names");
        StringAssert.Contains(Only(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par"), RecoveryBlockCount = 255 }), "at most 255");
    }

    [TestMethod]
    public void Plan_WithoutFiles_HasNoBlocks()
    {
        foreach (var output in new[] { "e.par", "e.par2", "e.par3" })
        {
            var plan = ParClient.Plan(new ParCreateRequest { Inputs = [], OutputPath = Path.Combine(_dir, output) });
            Assert.AreEqual(0, plan.InputBlocks, output);
            Assert.AreEqual(0, plan.RecoveryBlocks, output);
            Assert.IsEmpty(plan.RecoveryFiles, output);
        }

        // An empty file has no PAR2 blocks; unknown extensions plan as PAR2.
        var empty = Write("empty.bin", 0);
        var p2 = ParClient.Plan(new ParCreateRequest { Inputs = [empty], OutputPath = Path.Combine(_dir, "set.out") });
        Assert.AreEqual(ParFormat.Par2, p2.Format);
        Assert.AreEqual(0, p2.InputBlocks);
        Assert.AreEqual((1, 1), p2.RebuildableFiles, "an empty file is recreated from its description");
    }

    [TestMethod]
    public void Plan_Par3_MaxInputBlocks_UsesTheSmallestBlockSize_AndIsCapped()
    {
        var a = Write("a.bin", 64 * 10 + 50);
        var plan = ParClient.Plan(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par3") });
        Assert.AreEqual(11, plan.MaxInputBlocks, "10 full blocks of 64 bytes + a 50-byte tail");
        Assert.AreEqual(ParClient.Par3MaxPlannedBlocks, ParClient.Plan(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "s.par3") },
            [new ParPlanFile(a, 1L << 40, a)]).MaxInputBlocks);
    }

    [TestMethod]
    public void Create_Par3_HonorsTheRecoveryFileScheme()
    {
        var a = Write("a.bin", 50_000);
        var report = ParClient.Create(new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, "u.par3"), BlockSize = 1000, RecoveryBlockCount = 9, VolumeCount = 3, UniformVolumes = true });
        CollectionAssert.AreEqual(new[] { "u.par3", "u.vol00+03.par3", "u.vol03+03.par3", "u.vol06+03.par3" }, report.WrittenFiles.Select(Path.GetFileName).ToArray());
        Assert.AreEqual(RecoveryFileScheme.LimitSize, ParClient.SchemeOf(new ParCreateRequest { Inputs = [], OutputPath = "x", LimitVolumeSize = true }));
    }

    [TestMethod]
    public void ExistingSetFiles_FindAnEarlierSet_PerFormat()
    {
        var a = Write("a.bin", 20_000);
        foreach (var (output, extra) in new[] { ("old.par3", "old.vol99+01.par3"), ("old.par2", "old.vol99+01.par2"), ("old.par", "old.p07") })
        {
            var request = new ParCreateRequest { Inputs = [a], OutputPath = Path.Combine(_dir, output), RecoveryBlockCount = 2 };
            Assert.IsEmpty(ParClient.ExistingSetFiles(request.OutputPath), output);
            Assert.IsFalse(ParClient.Plan(request).Warnings.Any(w => w.StartsWith("There are already", StringComparison.Ordinal)));
            ParClient.Create(request);
            File.WriteAllBytes(Path.Combine(_dir, extra), [1]);
            var existing = ParClient.ExistingSetFiles(request.OutputPath);
            Assert.Contains(Path.Combine(_dir, extra), existing);
            Assert.Contains(Path.GetFullPath(request.OutputPath), existing);
            StringAssert.Contains(string.Join(" ", ParClient.Plan(request).Warnings), $"There are already {existing.Count} PAR file(s) named old… here");
        }

        Assert.IsEmpty(ParClient.ExistingSetFiles(Path.Combine(_dir, "nowhere", "x.par")));
    }
}

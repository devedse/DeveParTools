using DeveParTools.Core.Operations;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>Adding recovery blocks to an existing set: Cauchy (both conventions) and FFT, limits and failures.</summary>
[TestClass]
public sealed class ExtendTests
{
    private const int Size = 64 * 1024;

    private static (List<byte[]> Contents, string[] Paths) Files(TempDir dir, int count = 3)
    {
        var contents = Enumerable.Range(0, count).Select(i => TestData.Random(500 + i, Size - i * 777)).ToList();
        var paths = contents.Select((c, i) => dir.Write($"in{i}.bin", c)).ToArray();
        return (contents, paths);
    }

    /// <summary>Deletes the original recovery volumes, damages data and checks that repair succeeds with the new blocks alone.</summary>
    private static void AssertNewBlocksRepair(TempDir dir, IReadOnlyList<string> originalVolumes, List<byte[]> contents, int bytesToDestroy)
    {
        foreach (var v in originalVolumes)
        {
            File.Delete(v);
        }

        var damaged = (byte[])contents[0].Clone();
        Array.Clear(damaged, 1000, bytesToDestroy);
        File.WriteAllBytes(dir["in0.bin"], damaged);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.IsTrue(verify.RepairPossible, $"missing {verify.MissingBlocks}, recovery {verify.RecoveryBlocksAvailable}");
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true });
        Assert.IsTrue(repair.Success, repair.Message);
        CollectionAssert.AreEqual(contents[0], dir.Read("in0.bin"));
    }

    [TestMethod]
    [DataRow(Par3CauchyConvention.Par3cmdline)]
    [DataRow(Par3CauchyConvention.Specification)]
    public void Cauchy_DetectsTheConvention_AndNewBlocksRepair(Par3CauchyConvention convention)
    {
        using var dir = new TempDir();
        var (contents, paths) = Files(dir);
        var created = Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 4096, RecoveryBlockCount = 4, Code = Par3Code.Cauchy, CauchyConvention = convention });

        var stages = new List<string>();
        var context = new OperationContext { Observer = new StageObserver(stages) };
        var extended = Par3Extender.Extend(new Par3ExtendOptions { Par3Path = created.WrittenFiles[1], RecoveryBlockCount = 6, VolumeCount = 2 }, context);
        Assert.AreEqual(6, extended.RecoveryBlocks);
        Assert.AreEqual(created.SetId, extended.SetId);
        Assert.AreEqual(2, extended.WrittenFiles.Count);
        StringAssert.Contains(extended.WrittenFiles[0], "set.vol04+02.par3");
        StringAssert.Contains(extended.WrittenFiles[1], "set.vol06+04.par3");
        Assert.AreEqual(convention == Par3CauchyConvention.Specification, stages.Contains("Encoding (specification convention)"), string.Join(", ", stages));
        Assert.IsFalse(Directory.EnumerateFiles(dir.Path, "*.tmp").Any(), "the probe file is removed");

        Assert.AreEqual(10, Par3Files.Verify(dir["set.par3"]).RecoveryBlocksAvailable);
        AssertNewBlocksRepair(dir, created.WrittenFiles.Skip(1).ToList(), contents, 5 * 4096);
    }

    [TestMethod]
    public void Cauchy_WithoutExistingRecovery_UsesPar3cmdlineConvention()
    {
        using var dir = new TempDir();
        var (contents, paths) = Files(dir);
        Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 4096, RecoveryBlockCount = 0, Code = Par3Code.Cauchy });
        var extended = Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RedundancyPercent = 20 });
        Assert.AreEqual(10, extended.RecoveryBlocks, "ceil(48 blocks × 20%)");
        AssertNewBlocksRepair(dir, [], contents, 3 * 4096);
    }

    [TestMethod]
    public void Fft_ExtendsUpToTheMaximum()
    {
        using var dir = new TempDir();
        var (contents, paths) = Files(dir);
        var created = Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 4096, RecoveryBlockCount = 2, MaxRecoveryBlocks = 8, Code = Par3Code.Fft });
        StringAssert.Contains(created.CodeDescription, "FFT");

        var ex = Assert.ThrowsExactly<ArgumentException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 7 }));
        StringAssert.Contains(ex.Message, "at most 8 recovery blocks");
        StringAssert.Contains(ex.Message, "at most 6 more");

        var extended = Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 6 });
        Assert.AreEqual(6, extended.RecoveryBlocks);
        StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 1, FirstRecoveryBlock = 9 })).Message, "at most 0 more");
        AssertNewBlocksRepair(dir, created.WrittenFiles.Skip(1).ToList(), contents, 5 * 4096);
    }

    [TestMethod]
    public void CustomFirstIndex_OutputName_AndClashes()
    {
        using var dir = new TempDir();
        var (_, paths) = Files(dir, 2);
        Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 4096, RecoveryBlockCount = 3, Code = Par3Code.Cauchy });

        var clash = Assert.ThrowsExactly<ArgumentException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 2, FirstRecoveryBlock = 2 }));
        StringAssert.Contains(clash.Message, "Recovery block 2 already exists");

        var extended = Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 2, FirstRecoveryBlock = 100, OutputPath = dir["more.par3"] });
        StringAssert.EndsWith(extended.WrittenFiles[0], "more.vol100+01.par3");

        // "more.*" is not part of the set (other base name), so index 100 is free again, but its file exists: refuse to overwrite.
        Assert.ThrowsExactly<IOException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 2, FirstRecoveryBlock = 100, OutputPath = dir["more.par3"] }));
        Assert.ThrowsExactly<ArgumentException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 0 }));
    }

    [TestMethod]
    public void RefusesWhenInputIsMissing_OrTheFieldIsFull()
    {
        using var dir = new TempDir();
        var (_, paths) = Files(dir, 2);
        Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 4096, RecoveryBlockCount = 2, Code = Par3Code.Cauchy });

        // 31 blocks + 2 recovery fit GF(2^8): index 250 does not.
        var full = Assert.ThrowsExactly<ArgumentException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 1, FirstRecoveryBlock = 250 }));
        StringAssert.Contains(full.Message, "GF(2^8)");

        File.Delete(paths[1]);
        Assert.ThrowsExactly<InvalidOperationException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 1 }));
    }

    [TestMethod]
    public void IncompatibleRecoveryData_IsRejected_AndCleanedUp()
    {
        using var dir = new TempDir();
        var (_, paths) = Files(dir, 2);
        var created = Par3Creator.Create(new Par3CreateOptions { Inputs = paths, OutputPath = dir["set.par3"], BlockSize = 4096, RecoveryBlockCount = 1, Code = Par3Code.Cauchy });

        // Replace the stored recovery block's data with garbage but keep its packet checksum valid.
        var volume = created.WrittenFiles[1];
        var packet = Par3Files.Packets(volume).OfType<DeveParTools.Par3.Packets.Par3RecoveryDataPacket>().Single();
        TestData.RewritePacket(volume, packet.SourceOffset, raw => raw.AsSpan(raw.Length - 100).Fill(0xAB));

        var ex = Assert.ThrowsExactly<InvalidDataException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 2 }));
        StringAssert.Contains(ex.Message, "either Cauchy convention");
        Assert.AreEqual(2, Directory.EnumerateFiles(dir.Path, "set*.par3").Count(), "no new volumes are left behind");
        Assert.IsFalse(Directory.EnumerateFiles(dir.Path, "*.tmp").Any());
    }

    [TestMethod]
    public void RefusesSetsWithoutAWholeSetMatrix()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        var content = TestData.Random(9, 256);
        b.Root([b.File("a.bin", content)]);
        dir.Write("a.bin", content);
        b.Sparse([0], 4, 2, 7);
        b.WriteAll(dir["set.par3"]);
        Assert.ThrowsExactly<NotSupportedException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 1 }));
    }

    [TestMethod]
    [DataRow("/x/set.vol10+05.par3", "/x/set.par3")]
    [DataRow("/x/my.set.PART0+3.PAR3", "/x/my.set.par3")]
    [DataRow("/x/set.par3", "/x/set.par3")]
    [DataRow("/x/set.vol1.par3", "/x/set.vol1.par3")]
    public void IndexPathOf(string input, string expected)
        => Assert.AreEqual(expected.Replace('/', Path.DirectorySeparatorChar), Par3Extender.IndexPathOf(input.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class StageObserver(List<string> stages) : IParOperationObserver
    {
        public void Stage(string name) => stages.Add(name);

        public void Progress(double fraction, string? detail)
        {
        }

        public void Log(ParLogLevel level, string message)
        {
        }
    }

    [TestMethod]
    public void SetWithoutInputBlocks_CannotBeExtended()
    {
        using var dir = new TempDir();
        dir.Write("empty.bin", []);
        Par3Creator.Create(new Par3CreateOptions { Inputs = [dir["empty.bin"]], OutputPath = dir["set.par3"], BlockSize = 1024, RecoveryBlockCount = 0 });
        Assert.ThrowsExactly<InvalidOperationException>(() => Par3Extender.Extend(new Par3ExtendOptions { Par3Path = dir["set.par3"], RecoveryBlockCount = 1 }));
    }
}

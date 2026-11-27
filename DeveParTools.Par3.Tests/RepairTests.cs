using DeveParTools.Core.Galois;
using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>Repair: FFT and matrix codes (Cauchy, Sparse, Explicit, mixed, incremental), backups and failures.</summary>
[TestClass]
public sealed class RepairTests
{
    private const ulong Bs = 64;

    private static Par3RepairResult Repair(string par3, Par3RepairOptions? options = null, OperationContext? context = null)
    {
        context ??= new OperationContext();
        return Par3Repairer.Repair(Par3Files.Verify(par3, context), options ?? new Par3RepairOptions { PurgeBackups = true }, context);
    }

    /// <summary>A builder set with one file per block ("f0.bin", "f1.bin", ...), written to disk.</summary>
    private static (Par3SetBuilder Builder, List<byte[]> Contents) BlockFiles(TempDir dir, int count, byte[] generator, ulong blockSize = Bs)
    {
        var b = new Par3SetBuilder(blockSize, generator);
        var contents = Enumerable.Range(0, count).Select(i => TestData.Random(100 + i, (int)blockSize)).ToList();
        b.Root(contents.Select((c, i) => b.File($"f{i}.bin", c)).ToList());
        for (int i = 0; i < count; i++)
        {
            dir.Write($"f{i}.bin", contents[i]);
        }

        return (b, contents);
    }

    private static void AssertFiles(TempDir dir, List<byte[]> contents)
    {
        for (int i = 0; i < contents.Count; i++)
        {
            CollectionAssert.AreEqual(contents[i], dir.Read($"f{i}.bin"), $"f{i}.bin");
        }
    }

    [TestMethod]
    public void NothingToDo()
    {
        using var dir = new TempDir();
        var (b, _) = BlockFiles(dir, 2, [0x1D]);
        b.WriteAll(dir["set.par3"]);
        var r = Repair(dir["set.par3"]);
        Assert.IsTrue(r.Success);
        Assert.AreEqual("All files are already correct.", r.Message);
        Assert.AreEqual(0, r.RepairedFiles.Count);
    }

    [TestMethod]
    public void Backups_AreNumberedUnlessPurged()
    {
        using var dir = new TempDir();
        var content = TestData.Random(1, 3000);
        dir.Write("a.bin", content);
        Par3Creator.Create(new Par3CreateOptions { Inputs = [dir["a.bin"]], OutputPath = dir["set.par3"], BlockSize = 1024, RecoveryBlockCount = 1 });
        dir.Write("a.bin.1", [7]);
        TestData.Corrupt(dir["a.bin"], 10);
        var damaged = dir.Read("a.bin");

        var context = new OperationContext();
        var r = Repair(dir["set.par3"], new Par3RepairOptions(), context);
        Assert.IsTrue(r.Success, r.Message);
        Assert.AreEqual(dir["a.bin"], r.RepairedFiles.Single());
        Assert.AreEqual(1, r.BlocksReconstructed);
        Assert.AreEqual("Repaired 1 file(s).", r.Message);
        Assert.AreEqual(BlockState.Repaired, context.Blocks!.Get(0));
        CollectionAssert.AreEqual(content, dir.Read("a.bin"));
        CollectionAssert.AreEqual(damaged, dir.Read("a.bin.2"), "a.bin.1 was taken");
        CollectionAssert.AreEqual(new byte[] { 7 }, dir.Read("a.bin.1"));
        Assert.AreEqual(dir["a.bin.3"], Par3Repairer.NextBackupName(dir["a.bin"]));
        Assert.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length, "the temporary block file is removed");
    }

    [TestMethod]
    public void ForgedRecoveryData_RebuiltBlocksAreRejected()
    {
        // A recovery packet with a valid checksum but wrong data: neither Cauchy convention reproduces the block
        // checksums, the file hash check refuses the result and the damaged file is left alone.
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 4096));
        var created = Par3Creator.Create(new Par3CreateOptions { Inputs = [dir["a.bin"]], OutputPath = dir["set.par3"], BlockSize = 1024, RecoveryBlockCount = 1 });
        var volume = created.WrittenFiles[1];
        var rec = Par3Files.Packets(volume).OfType<Par3RecoveryDataPacket>().Single();
        TestData.RewritePacket(volume, rec.SourceOffset, p => p[48 + 40 + 5] ^= 1);
        TestData.Corrupt(dir["a.bin"], 2000);
        var damaged = dir.Read("a.bin");

        var observer = new RecordingObserver();
        var r = Repair(dir["set.par3"], context: new OperationContext { Observer = observer });
        Assert.IsFalse(r.Success);
        Assert.AreEqual(dir["a.bin"], r.FailedFiles.Single());
        Assert.AreEqual("1 file(s) failed verification after repair.", r.Message);
        Assert.IsTrue(observer.Warnings.Contains("Rebuilt blocks do not match their checksums."));
        CollectionAssert.AreEqual(damaged, dir.Read("a.bin"));
        Assert.IsFalse(File.Exists(dir["a.bin.deveparttools.tmp"]));
    }

    [TestMethod]
    public void Fft_LossConcentratedInOneCohort_IsNotRepairable()
    {
        // 2 cohorts, 4 recovery blocks (2 per cohort). Losing 3 blocks of cohort 0 cannot be repaired although
        // 3 ≤ 4: the verifier must say so instead of letting the repair fail.
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 20 * 64));
        Par3Creator.Create(new Par3CreateOptions { Inputs = [dir["a.bin"]], OutputPath = dir["set.par3"], BlockSize = 64, RecoveryBlockCount = 4, Code = Par3Code.Fft, Interleave = 1 });
        foreach (int block in new[] { 0, 2, 4 })
        {
            TestData.Corrupt(dir["a.bin"], block * 64);
        }

        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(3, verify.MissingBlocks);
        Assert.AreEqual(4, verify.RecoveryBlocksAvailable);
        Assert.IsFalse(verify.RepairPossible);
        var r = Par3Repairer.Repair(verify);
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Message, "each FFT cohort only rebuilds its own blocks");

        // Two per cohort is fine.
        dir.Write("a.bin", TestData.Random(1, 20 * 64));
        foreach (int block in new[] { 0, 2, 5, 7 })
        {
            TestData.Corrupt(dir["a.bin"], block * 64);
        }

        Assert.IsTrue(Repair(dir["set.par3"]).Success);
        CollectionAssert.AreEqual(TestData.Random(1, 20 * 64), dir.Read("a.bin"));
    }

    [TestMethod]
    public void Fft_UnsupportedPacketParameters_FallBackToMatrixRecovery()
    {
        // An FFT matrix packet Leopard cannot use (m = 2^20): its recovery blocks are not counted as usable.
        using var dir = new TempDir();
        var (b, _) = BlockFiles(dir, 3, [0x1D]);
        var fft = Par3PacketWriter.Fft(b.SetId, 0, 0, 20, 0);
        var body = new byte[40 + (int)Bs];
        b.RootChecksum.WriteTo(body);
        Par3PacketWriter.ChecksumOf(fft).WriteTo(body.AsSpan(16));
        Par3SetBuilder.Write(dir["set.par3"], [.. b.IndexPackets(), fft, Par3PacketWriter.Build(b.SetId, Par3PacketTypes.RecoveryData, body)]);
        File.Delete(dir["f1.bin"]);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(1, verify.RecoveryBlocksAvailable);
        Assert.IsFalse(verify.RepairPossible);
    }

    [TestMethod]
    [DataRow(new byte[] { 0x1D }, DisplayName = "GF(2^8)")]
    [DataRow(new byte[] { 0x0B, 0x10 }, DisplayName = "GF(2^16)")]
    [DataRow(new byte[] { 0x8D, 0, 0, 0 }, DisplayName = "GF(2^32)")]
    public void Sparse_RebuildsMissingBlocks(byte[] generator)
    {
        using var dir = new TempDir();
        var (b, contents) = BlockFiles(dir, 8, generator);
        b.Sparse([0, 1, 2, 3, 4, 5], maxRecovery: 6, nonZero: 4, seed: 1234);
        b.WriteAll(dir["set.par3"]);
        foreach (int i in new[] { 1, 4, 6 })
        {
            File.Delete(dir[$"f{i}.bin"]);
        }

        var r = Repair(dir["set.par3"]);
        Assert.IsTrue(r.Success, r.Message);
        Assert.AreEqual(3, r.BlocksReconstructed);
        AssertFiles(dir, contents);
    }

    [TestMethod]
    public void Sparse_DependentRows_FailGracefully()
    {
        // One non-zero element per input: find two inputs whose only non-zero is in the same recovery row. Then the
        // other rows cannot tell them apart, whatever the count of recovery blocks.
        using var dir = new TempDir();
        var rows = Par3Sparse.Generate(new Gf8Field(GaloisField8.Default), new Par3SparseMatrixPacket(0, 0, 4, 1, 99), 8);
        var column = rows.Select(r => Array.FindIndex(r, v => v != 0)).ToList();
        var pair = Enumerable.Range(0, 8).SelectMany(i => Enumerable.Range(i + 1, 7 - i).Select(j => (i, j))).First(p => column[p.i] == column[p.j]);

        var (b, _) = BlockFiles(dir, 8, [0x1D]);
        b.Sparse([0, 1, 2, 3], maxRecovery: 4, nonZero: 1, seed: 99);
        b.WriteAll(dir["set.par3"]);
        File.Delete(dir[$"f{pair.i}.bin"]);
        File.Delete(dir[$"f{pair.j}.bin"]);

        var context = new OperationContext { Observer = new RecordingObserver() };
        var verify = Par3Files.Verify(dir["set.par3"], context);
        Assert.IsTrue(verify.RepairPossible, "2 missing, 4 recovery blocks: possible by count");
        var r = Par3Repairer.Repair(verify, null, context);
        Assert.IsFalse(r.Success);
        StringAssert.StartsWith(r.Message, "Repair failed: The available recovery blocks are not independent enough");
        Assert.AreEqual(BlockState.Unrecoverable, context.Blocks!.Get(pair.i));
        Assert.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length);
    }

    [TestMethod]
    public void Explicit_SingleRowWithGivenFactors()
    {
        using var dir = new TempDir();
        var (b, contents) = BlockFiles(dir, 4, [0x1D]);
        b.Explicit([(0, [3]), (2, [5]), (3, [7])]);
        b.Explicit([(1, [9])], recoveryIndices: [1]); // only index 0 is a valid row of an Explicit matrix
        b.WriteAll(dir["set.par3"]);
        File.Delete(dir["f2.bin"]);
        Assert.IsTrue(Repair(dir["set.par3"]).Success);
        AssertFiles(dir, contents);

        // Block 1 is only in the row with index 1, which has no coefficients: not solvable.
        File.Delete(dir["f1.bin"]);
        var r = Repair(dir["set.par3"]);
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void MixedMatrices_CauchyRangesSparseAndExplicit()
    {
        using var dir = new TempDir();
        var (b, contents) = BlockFiles(dir, 10, [0x0B, 0x10]);
        b.Cauchy([0], first: 0, lastPlus1: 5);  // blocks 0..4
        b.Cauchy([0, 1], first: 5);             // blocks 5..
        b.Sparse([0], maxRecovery: 3, nonZero: 3, seed: 5, first: 2, lastPlus1: 8);
        b.Explicit([(9, [1, 0])]);
        b.WriteAll(dir["set.par3"]);
        foreach (int i in new[] { 1, 5, 9, 3 })
        {
            File.Delete(dir[$"f{i}.bin"]);
        }

        var r = Repair(dir["set.par3"]);
        Assert.IsTrue(r.Success, r.Message);
        AssertFiles(dir, contents);
    }

    [TestMethod]
    [DataRow(Par3CauchyConvention.Par3cmdline)]
    [DataRow(Par3CauchyConvention.Specification)]
    public void Cauchy_BothConventionsFromAForeignCreator(Par3CauchyConvention convention)
    {
        using var dir = new TempDir();
        var (b, contents) = BlockFiles(dir, 6, [0x1D]);
        b.Cauchy([0, 1, 2], convention);
        b.WriteAll(dir["set.par3"]);
        foreach (int i in new[] { 0, 3, 5 })
        {
            File.Delete(dir[$"f{i}.bin"]);
        }

        Assert.IsTrue(Repair(dir["set.par3"]).Success);
        AssertFiles(dir, contents);
    }

    [TestMethod]
    public void Incremental_ParentRecoveryOnlyCoversParentBlocks()
    {
        using var dir = new TempDir();
        var parent = new Par3SetBuilder(Bs, [0x1D], unique: 1);
        var old = Enumerable.Range(0, 3).Select(i => TestData.Random(10 + i, (int)Bs)).ToList();
        var oldFiles = old.Select((c, i) => parent.File($"old{i}.bin", c)).ToList();
        parent.Root(oldFiles);
        parent.Cauchy([0, 1]);
        parent.WriteAll(dir["parent.par3"]);

        var child = new Par3SetBuilder(Bs, [0x1D], unique: 2, parent: parent.SetId, parentRoot: parent.RootChecksum, firstNewBlock: parent.LowestUnused);
        foreach (var (i, data) in parent.Blocks)
        {
            child.SetBlock(i, data);
        }

        var added = TestData.Random(20, (int)Bs);
        child.Root([oldFiles[0], oldFiles[2], child.File("new.bin", added)]); // old1.bin was deleted from the input set
        child.Cauchy([0]);
        child.WriteAll(dir["child.par3"]);
        dir.Write("old0.bin", old[0]);
        dir.Write("old2.bin", old[2]);
        dir.Write("new.bin", added);

        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["child.par3"], ExtraPar3Files = [dir["parent.par3"]] });
        CollectionAssert.AreEquivalent(new[] { "old0.bin", "old2.bin", "new.bin" }, verify.Files.Select(f => f.File.RelativePath).ToArray());
        Assert.AreEqual(3, verify.RecoveryBlocksAvailable);
        Assert.AreEqual(1, verify.MissingBlocks, "old1.bin's block is still an unknown of the parent's recovery data");

        // Losing a new block and an old one: the child's row covers both, the parent's rows only the old one.
        File.Delete(dir["new.bin"]);
        File.Delete(dir["old2.bin"]);
        verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["child.par3"], ExtraPar3Files = [dir["parent.par3"]] });
        Assert.AreEqual(3, verify.MissingBlocks);
        var r = Par3Repairer.Repair(verify);
        Assert.IsTrue(r.Success, r.Message);
        CollectionAssert.AreEqual(added, dir.Read("new.bin"));
        CollectionAssert.AreEqual(old[2], dir.Read("old2.bin"));

        // Without the child's recovery block the new block cannot be rebuilt: the parent's rows are zero there.
        File.Delete(dir["new.bin"]);
        Par3SetBuilder.Write(dir["child.par3"], child.IndexPackets());
        verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = dir["child.par3"], ExtraPar3Files = [dir["parent.par3"]] });
        Assert.AreEqual(2, verify.MissingBlocks);
        Assert.IsTrue(verify.RepairPossible, "two missing, two (parent) recovery blocks");
        Assert.IsFalse(Par3Repairer.Repair(verify).Success);
    }

    [TestMethod]
    public void ShortRecoveryData_IsZeroPadded()
    {
        // "If the data's length is less than the block size, the rest of the block is filled with zero bytes."
        using var dir = new TempDir();
        var b = new Par3SetBuilder(Bs, [0x1D]);
        var contents = Enumerable.Range(0, 3).Select(i => (byte[])[.. TestData.Random(i, 40), .. new byte[Bs - 40]]).ToList();
        b.Root(contents.Select((c, i) => b.File($"f{i}.bin", c)).ToList());
        b.Cauchy([0]);
        var rec = b.RecoveryPackets().Single();
        var truncated = Par3PacketWriter.Build(b.SetId, Par3PacketTypes.RecoveryData, rec.AsSpan(48, 40 + 40));
        Par3SetBuilder.Write(dir["set.par3"], [.. b.IndexPackets(), truncated]);
        dir.Write("f0.bin", contents[0]);
        dir.Write("f2.bin", contents[2]);
        Assert.IsTrue(Repair(dir["set.par3"]).Success);
        CollectionAssert.AreEqual(contents[1], dir.Read("f1.bin"));
    }

    [TestMethod]
    public void Coefficients_OutsideCoverageAndForFftRowsAreZero()
    {
        using var dir = new TempDir();
        var (b, _) = BlockFiles(dir, 3, [0x1D]);
        b.Cauchy([0]);
        b.WriteAll(dir["set.par3"]);
        var set = Par3Files.Load(dir["set.par3"]);
        var field = new Gf8Field(GaloisField8.Default);
        var c = new Par3Repairer.Par3Coefficients<byte>(field, set, Par3CauchyConvention.Par3cmdline);
        var cauchy = set.RecoveryBlocks.Single();
        Assert.AreEqual(Par3Cauchy.Coefficient(field, 0, 2), c.Get(cauchy, 2));
        Assert.AreEqual((byte)0, c.Get(cauchy with { CoveredBlocks = 2 }, 2), "inputs beyond the Root's lowest unused index");
        Assert.AreEqual((byte)0, c.Get(cauchy with { Matrix = new Par3FftMatrixPacket(0, 0, 1, 0) }, 1));
        Assert.AreEqual((byte)0, c.Get(cauchy with { Matrix = new Par3SparseMatrixPacket(0, 0, 1, 1, 1), Index = 3 }, 1), "index beyond the sparse matrix");
        Assert.AreEqual((byte)0, c.Get(cauchy with { Matrix = new Par3SparseMatrixPacket(5, 0, 2, 1, 1) }, 1), "input outside the sparse range");
    }

    [TestMethod]
    public void Cancellation_LeavesNoTemporaryFiles()
    {
        using var dir = new TempDir();
        var (b, _) = BlockFiles(dir, 4, [0x1D]);
        b.Cauchy([0]);
        b.WriteAll(dir["set.par3"]);
        File.Delete(dir["f0.bin"]);
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.Throws<OperationCanceledException>(() => Par3Repairer.Repair(verify, null, new OperationContext { CancellationToken = new CancellationToken(true) }));
        Assert.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length);
        Assert.IsFalse(File.Exists(dir["f0.bin"]));
    }
}

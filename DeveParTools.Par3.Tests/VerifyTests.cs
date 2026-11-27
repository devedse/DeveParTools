using DeveParTools.Core.Hashing;
using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>Verification: in-place checks, the rolling block search, the tail search, block states.</summary>
[TestClass]
public sealed class VerifyTests
{
    private const ulong Bs = 1024;

    private static Par3CreateResult Create(TempDir dir, int recovery = 4, IReadOnlyList<string>? inputs = null, bool storeData = false, Par3Dedup dedup = Par3Dedup.None)
        => Par3Creator.Create(new Par3CreateOptions { Inputs = inputs ?? [dir.Path], OutputPath = dir["set.par3"], BlockSize = Bs, RecoveryBlockCount = recovery, Code = Par3Code.Cauchy, StoreData = storeData, Dedup = dedup });

    private static Par3FileVerification File(Par3VerifyResult r, string name) => r.Files.Single(f => f.File.RelativePath == name);

    [TestMethod]
    public void IntactSet_AllFound()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 5000));
        dir.Write("sub/b.bin", TestData.Random(2, 30));
        Create(dir);
        var observer = new RecordingObserver();
        var context = new OperationContext { Observer = observer };
        var r = Par3Files.Verify(dir["set.par3"], context);
        Assert.IsTrue(r.AllFilesComplete);
        Assert.IsTrue(r.RepairPossible);
        Assert.AreEqual(0, r.MissingBlocks);
        Assert.AreEqual(4, r.RecoveryBlocksAvailable);
        Assert.AreEqual(dir.Path, r.BaseDirectory);
        Assert.AreEqual(dir["sub/b.bin"], File(r, "sub/b.bin").TargetPath);
        Assert.IsTrue(Enumerable.Range(0, 5).All(i => context.Blocks!.Get(i) == BlockState.Found));
        Assert.IsTrue(Enumerable.Range(0, 4).All(i => context.Blocks!.GetRecovery(i) == RecoveryBlockState.Available));
        CollectionAssert.AreEquivalent(new[] { "set.vol00+01.par3", "set.vol01+02.par3", "set.vol01+02.par3", "set.vol03+01.par3" }, context.Blocks!.RecoveryContainer.Values.ToArray());
        Assert.AreEqual(dir["a.bin"], context.Blocks.Ownership[1].Single().File);
        Assert.AreEqual("All files are correct.", observer.Logs.Last().Message);
        CollectionAssert.AreEqual(new[] { "Loading", "Verifying" }, observer.Stages);
    }

    [TestMethod]
    public void InsertedBytes_BlocksAndTailAreFoundShifted()
    {
        using var dir = new TempDir();
        var original = TestData.Random(1, 6 * 1024 + 500);
        dir.Write("a.bin", original);
        Create(dir, recovery: 0);
        TestData.Insert(dir["a.bin"], 2048, [1, 2, 3, 4, 5]); // between blocks 1 and 2

        var observer = new RecordingObserver();
        var context = new OperationContext { Observer = observer };
        var r = Par3Files.Verify(dir["set.par3"], context);
        Assert.AreEqual(Par3FileStatus.Damaged, File(r, "a.bin").Status);
        Assert.AreEqual(0, r.MissingBlocks, "every block and the tail are found at their new offsets");
        Assert.AreEqual(BlockState.Found, context.Blocks!.Get(0));
        Assert.AreEqual(BlockState.FoundMoved, context.Blocks.Get(3));
        Assert.AreEqual(1024 * 3 + 5, context.Blocks.FoundAt[3].Offset);
        Assert.AreEqual(BlockState.FoundElsewhere, context.Blocks.Get(6), "the tail block is assembled from its found tail");
        Assert.IsTrue(observer.Stages.Contains("Scanning"));

        var repair = Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true });
        Assert.IsTrue(repair.Success, repair.Message);
        Assert.AreEqual(0, repair.BlocksReconstructed);
        CollectionAssert.AreEqual(original, dir.Read("a.bin"));
    }

    [TestMethod]
    public void RenamedFile_IsFoundAsExtraDataFile()
    {
        using var dir = new TempDir();
        var a = TestData.Random(1, 3 * 1024 + 700);
        var tiny = TestData.Random(2, 20);
        dir.Write("a.bin", a);
        dir.Write("tiny.bin", tiny);
        Create(dir, recovery: 0);
        System.IO.File.Move(dir["a.bin"], dir["renamed.bin"]);
        System.IO.File.Delete(dir["tiny.bin"]);

        var context = new OperationContext();
        var r = Par3Files.Verify(dir["set.par3"], context, extraData: [dir["renamed.bin"], dir["does-not-exist.bin"]]);
        Assert.AreEqual(Par3FileStatus.Missing, File(r, "a.bin").Status);
        Assert.AreEqual(0, r.MissingBlocks);
        Assert.AreEqual(BlockState.FoundElsewhere, context.Blocks!.Get(1));
        Assert.AreEqual(dir["renamed.bin"], r.Tails.Single().Value.Path);

        Assert.IsTrue(Par3Repairer.Repair(r).Success);
        CollectionAssert.AreEqual(a, dir.Read("a.bin"));
        CollectionAssert.AreEqual(tiny, dir.Read("tiny.bin"), "inline tails come from the File packet");
    }

    [TestMethod]
    public void TailInsideALargeExtraFile_IsFoundByTheRollingCrc()
    {
        // The tail sits beyond the first 4 MiB read buffer of the tail search, at an odd offset.
        using var dir = new TempDir();
        var b = TestData.Random(2, 1024 + 333);
        dir.Write("b.bin", b);
        Create(dir, recovery: 0);
        System.IO.File.Delete(dir["b.bin"]);
        var junk = TestData.Random(3, (4 << 20) + 5000);
        b.AsSpan(1024).CopyTo(junk.AsSpan((4 << 20) + 101));
        b.AsSpan(0, 1024).CopyTo(junk.AsSpan(77));
        var extra = dir.Write("junk.dat", junk);
        var tooShort = dir.Write("short.dat", b.AsSpan(1024, 39).ToArray());

        var r = Par3Files.Verify(dir["set.par3"], extraData: [tooShort, extra]);
        Assert.AreEqual(0, r.MissingBlocks);
        Assert.AreEqual(new PieceSource(extra, (4 << 20) + 101), r.Tails.Single().Value);
        Assert.IsTrue(Par3Repairer.Repair(r).Success);
        CollectionAssert.AreEqual(b, dir.Read("b.bin"));
    }

    [TestMethod]
    public void CorruptedFullBlock_TailStillFoundInPlace()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 3 * 1024 + 100));
        Create(dir, recovery: 2);
        TestData.Corrupt(dir["a.bin"], 1500);

        var context = new OperationContext();
        var r = Par3Files.Verify(dir["set.par3"], context);
        Assert.AreEqual(1, r.MissingBlocks);
        Assert.AreEqual(BlockState.Recoverable, context.Blocks!.Get(1));
        Assert.AreEqual(dir["a.bin"], r.Tails.Single().Value.Path, "the tail was found by the in-place check, no tail search needed");
        Assert.AreEqual(1, Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true }).BlocksReconstructed);
    }

    [TestMethod]
    public void TruncatedAndExtendedFiles_AreDamaged()
    {
        using var dir = new TempDir();
        var a = TestData.Random(1, 4 * 1024);
        var b = TestData.Random(2, 2048 + 50);
        dir.Write("a.bin", a);
        dir.Write("b.bin", b);
        Create(dir, recovery: 2);
        dir.Write("a.bin", a[..3000]);
        dir.Write("b.bin", [.. b, 9, 9, 9]);

        var observer = new RecordingObserver();
        var r = Par3Files.Verify(dir["set.par3"], new OperationContext { Observer = observer });
        Assert.AreEqual(Par3FileStatus.Damaged, File(r, "a.bin").Status);
        Assert.AreEqual(Par3FileStatus.Damaged, File(r, "b.bin").Status, "all blocks are fine, but the length is wrong");
        Assert.AreEqual(2, r.MissingBlocks, "a.bin's block 2 is cut short and block 3 is gone");
        StringAssert.Contains(observer.Logs.Last().Message, "repair is possible");

        Assert.IsTrue(Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(a, dir.Read("a.bin"));
        CollectionAssert.AreEqual(b, dir.Read("b.bin"));
    }

    [TestMethod]
    public void NotEnoughRecovery_BlocksAreUnrecoverable()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(1, 8 * 1024));
        Create(dir, recovery: 2);
        TestData.Corrupt(dir["a.bin"], 0, 3 * 1024);
        var observer = new RecordingObserver();
        var context = new OperationContext { Observer = observer };
        var r = Par3Files.Verify(dir["set.par3"], context);
        Assert.AreEqual(3, r.MissingBlocks);
        Assert.IsFalse(r.RepairPossible);
        Assert.AreEqual(BlockState.Unrecoverable, context.Blocks!.Get(0));
        StringAssert.Contains(observer.Logs.Last().Message, "is NOT possible");
        var repair = Par3Repairer.Repair(r);
        Assert.IsFalse(repair.Success);
        Assert.AreEqual("Repair not possible: 3 block(s) missing but only 2 recovery block(s) available.", repair.Message);
    }

    [TestMethod]
    public void IdenticalBlocksWithoutDedup_OneFoundCopyServesBoth()
    {
        using var dir = new TempDir();
        var same = TestData.Random(1, 1024);
        dir.Write("a.bin", [.. same, .. TestData.Random(2, 1024)]);
        dir.Write("b.bin", same);
        Create(dir, recovery: 1);
        System.IO.File.Delete(dir["b.bin"]);
        TestData.Corrupt(dir["a.bin"], 10);
        var copy = dir.Write("copy.dat", [.. TestData.Random(5, 3), .. same]);

        var r = Par3Files.Verify(dir["set.par3"], extraData: [copy]);
        Assert.AreEqual(0, r.MissingBlocks, "block 0 (a.bin) and block 2 (b.bin) have the same checksum");
        Assert.IsTrue(Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(same, dir.Read("b.bin"));
    }

    [TestMethod]
    public void ArchiveMode_DamagedTailIsSearched()
    {
        // All blocks come from Data packets, but the damaged file's tail is still searched for.
        using var dir = new TempDir();
        var a = TestData.Random(1, 1024 + 400);
        dir.Write("a.bin", a);
        Create(dir, recovery: 0, storeData: true);
        TestData.Corrupt(dir["a.bin"], 1300);
        var context = new OperationContext();
        var r = Par3Files.Verify(dir["set.par3"], context);
        Assert.AreEqual(0, r.MissingBlocks);
        Assert.AreEqual(0, r.Tails.Count);
        Assert.AreEqual(BlockState.FoundStored, context.Blocks!.Get(1), "the damaged tail block comes from the stored data");
        Assert.AreEqual(BlockState.Found, context.Blocks.Get(0));
        Assert.IsTrue(Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(a, dir.Read("a.bin"));
    }

    [TestMethod]
    public void BaseDirectoryAndExtraPar3Files()
    {
        using var data = new TempDir();
        using var pars = new TempDir();
        var a = TestData.Random(1, 3000);
        data.Write("a.bin", a);
        var created = Par3Creator.Create(new Par3CreateOptions { Inputs = [data["a.bin"]], OutputPath = pars["set.par3"], BaseDirectory = data.Path, BlockSize = Bs, RecoveryBlockCount = 3 });
        using var elsewhere = new TempDir();
        var volume = created.WrittenFiles.Single(f => f.EndsWith("set.vol01+02.par3", StringComparison.Ordinal));
        System.IO.File.Move(volume, elsewhere["moved.par3"]);
        TestData.Corrupt(data["a.bin"], 0, 2048);

        var r = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = pars["set.par3"], BaseDirectory = data.Path, ExtraPar3Files = [elsewhere["moved.par3"]] });
        Assert.AreEqual(data.Path, r.BaseDirectory);
        Assert.AreEqual(3, r.RecoveryBlocksAvailable);
        Assert.AreEqual(2, r.MissingBlocks);
        Assert.IsTrue(Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(a, data.Read("a.bin"));
    }

    [TestMethod]
    public void UnsafeNamesAndMissingOptions_AreReportedAndIgnored()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(Bs, [0x1D]);
        var good = b.File("good.bin", TestData.Random(1, 100), options: [Fingerprint.Compute("gone"u8)]);
        var evil = b.File("..", TestData.Random(2, 100));
        var nested = b.Directory("sub", [b.File("a\0b", TestData.Random(3, 5))]);
        b.Root([good, evil, nested]);
        b.Cauchy([0, 1]);
        b.WriteAll(dir["set.par3"]);
        dir.Write("good.bin", TestData.Random(1, 100));
        Directory.CreateDirectory(dir["sub"]);

        var observer = new RecordingObserver();
        var r = Par3Files.Verify(dir["set.par3"], new OperationContext { Observer = observer });
        Assert.AreEqual(Par3FileStatus.Complete, File(r, "good.bin").Status);
        Assert.AreEqual(string.Empty, File(r, "..").TargetPath);
        Assert.AreEqual(Par3FileStatus.Missing, File(r, "sub/a\0b").Status);
        Assert.AreEqual(1, observer.Warnings.Count(w => w.StartsWith("Option packet", StringComparison.Ordinal)));
        Assert.AreEqual(2, observer.Warnings.Count(w => w.StartsWith("Ignoring unsafe file name", StringComparison.Ordinal)));

        // Repair rebuilds the missing block but never writes outside the base directory.
        var repair = Par3Repairer.Repair(r);
        Assert.IsTrue(repair.Success, repair.Message);
        Assert.AreEqual(0, repair.RepairedFiles.Count);
        Assert.AreEqual(0, repair.FailedFiles.Count);
    }

    [TestMethod]
    public void UnprotectedChunk_IsHashedAsZeros()
    {
        // Spec example 6: a 10,000-byte file whose middle 6,000 bytes are not protected.
        using var dir = new TempDir();
        var content = TestData.Random(1, 10_000);
        var b = new Par3SetBuilder(2000, [0x1D]);
        b.Root([b.File("f.bin", content, [new ProtectedChunk(2000), new UnprotectedChunk(6000), new ProtectedChunk(2000)])]);
        b.Cauchy([0, 1]);
        b.WriteAll(dir["set.par3"]);
        Assert.AreEqual(2, b.Blocks.Count);

        var changed = (byte[])content.Clone();
        changed[5000] ^= 1; // unprotected: still complete
        dir.Write("f.bin", changed);
        Assert.IsTrue(Par3Files.Verify(dir["set.par3"]).AllFilesComplete);

        changed[9000] ^= 1; // protected
        dir.Write("f.bin", changed);
        var r = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(Par3FileStatus.Damaged, File(r, "f.bin").Status);
        Assert.IsTrue(Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true }).Success);
        var repaired = dir.Read("f.bin");
        CollectionAssert.AreEqual(content[..2000], repaired[..2000]);
        CollectionAssert.AreEqual(changed[2000..8000], repaired[2000..8000], "unprotected bytes are kept from the damaged file");
        CollectionAssert.AreEqual(content[8000..], repaired[8000..]);

        // Without an old file the unprotected bytes cannot be known: they are written as zeros.
        System.IO.File.Delete(dir["f.bin"]);
        r = Par3Files.Verify(dir["set.par3"]);
        Assert.IsTrue(Par3Repairer.Repair(r).Success);
        CollectionAssert.AreEqual(new byte[6000], dir.Read("f.bin")[2000..8000]);
    }

    [TestMethod]
    public void DataPacketBlocksWithoutExternalData_AreConfirmedByTheFileHash()
    {
        // The spec only requires External Data checksums for blocks that are not stored in Data packets.
        using var dir = new TempDir();
        var content = TestData.Random(1, 3 * 1024 + 500);
        var b = new Par3SetBuilder(Bs, [0x1D]);
        b.Root([b.File("f.bin", content)]);
        Par3SetBuilder.Write(dir["set.par3"], [.. b.IndexPackets(externalData: false), .. Enumerable.Range(0, 4).Select(i => b.DataPacket((ulong)i))]);
        dir.Write("f.bin", content);

        var context = new OperationContext();
        var r = Par3Files.Verify(dir["set.par3"], context);
        Assert.IsTrue(r.AllFilesComplete);
        Assert.AreEqual(BlockState.Found, context.Blocks!.Get(1));

        TestData.Corrupt(dir["f.bin"], 2000);
        r = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(Par3FileStatus.Damaged, File(r, "f.bin").Status);
        Assert.IsTrue(Par3Repairer.Repair(r, new Par3RepairOptions { PurgeBackups = true }).Success);
        CollectionAssert.AreEqual(content, dir.Read("f.bin"));
    }

    [TestMethod]
    public void LostExternalDataPacket_IntactFilesAreStillComplete()
    {
        // Every copy of the External Data packet for blocks 2.. is damaged: those blocks cannot be checked one by
        // one, but an intact file proves them by its hash.
        using var dir = new TempDir();
        var content = TestData.Random(1, 4 * 1024);
        var b = new Par3SetBuilder(Bs, [0x1D]);
        b.Root([b.File("f.bin", content)]);
        b.Cauchy([0, 1]);
        Par3SetBuilder.Write(dir["set.par3"], [.. b.IndexPackets(externalData: false), .. b.ExternalData(i => i < 2), .. b.RecoveryPackets()]);
        dir.Write("f.bin", content);

        var context = new OperationContext();
        var r = Par3Files.Verify(dir["set.par3"], context);
        Assert.IsTrue(r.AllFilesComplete);
        Assert.AreEqual(0, r.MissingBlocks);
        Assert.AreEqual(BlockState.Found, context.Blocks!.Get(3));

        // Damaged: the unchecked blocks count as missing, the checked ones are still found.
        TestData.Corrupt(dir["f.bin"], 100);
        r = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(3, r.MissingBlocks);
        Assert.IsFalse(r.RepairPossible);
    }

    [TestMethod]
    public void ShortDataPacket_IsZeroPadded()
    {
        // "If the data's length is less than the block size, the rest of the block is filled with zero bytes."
        using var dir = new TempDir();
        var content = new byte[Bs];
        TestData.Random(1, 100).CopyTo(content, 0);
        var b = new Par3SetBuilder(Bs, [0x1D]);
        b.Root([b.File("f.bin", content)]);
        Par3SetBuilder.Write(dir["set.par3"], [.. b.IndexPackets(), b.DataPacket(0, 100)]);
        var r = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(0, r.MissingBlocks);
        Assert.IsTrue(Par3Repairer.Repair(r).Success);
        CollectionAssert.AreEqual(content, dir.Read("f.bin"));
    }

    [TestMethod]
    public void BlockMatcher_RejectsCrc64CollisionsAndNeverMatchesTails()
    {
        using var dir = new TempDir();
        var block = TestData.Random(1, (int)Bs);
        dir.Write("a.bin", block);
        Create(dir, recovery: 1);
        var set = Par3Files.Load(dir["set.par3"]);
        var matcher = new Par3BlockMatcher(set);
        Assert.AreEqual((int)Bs, matcher.BlockSize);
        Assert.AreEqual(0, matcher.TailLengths.Count);
        Assert.AreEqual(-1, matcher.MatchTail(block));

        // CRC-64 is linear, so flipping a set of bits whose CRC contributions cancel gives the same CRC.
        var collision = Crc64Collision(block);
        Assert.AreEqual(Crc64Iso.Compute(block), Crc64Iso.Compute(collision));
        CollectionAssert.AreNotEqual(block, collision);
        ulong weak = matcher.Weak(collision);
        Assert.IsTrue(matcher.MayMatch(weak));
        Assert.AreEqual(-1, matcher.MatchFull(weak, collision), "same CRC-64, different BLAKE3");
        Assert.AreEqual(0, matcher.MatchFull(weak, block));
        Assert.AreEqual(-1, matcher.MatchFull(weak ^ 1, block));

        // In a real scan the collision is not taken for the block.
        System.IO.File.Delete(dir["a.bin"]);
        var r = Par3Files.Verify(dir["set.par3"], extraData: [dir.Write("collision.bin", collision)]);
        Assert.AreEqual(1, r.MissingBlocks);
    }

    /// <summary>A different message with the same CRC-64: XOR a combination of single-bit flips whose CRC deltas sum to zero.</summary>
    private static byte[] Crc64Collision(byte[] data)
    {
        // delta(bit i) = CRC(e_i) ^ CRC(0); 65 vectors in a 64-bit space are dependent: find the dependency by elimination.
        var zero = Crc64Iso.Compute(new byte[data.Length]);
        var basis = new List<(ulong Vector, UInt128 Combination)>();
        for (int bit = 0; bit < 65; bit++)
        {
            var e = new byte[data.Length];
            e[bit / 8] = (byte)(1 << (bit % 8));
            ulong v = Crc64Iso.Compute(e) ^ zero;
            UInt128 comb = UInt128.One << bit;
            foreach (var (bv, bc) in basis)
            {
                if ((v ^ bv) < v)
                {
                    v ^= bv;
                    comb ^= bc;
                }
            }

            if (v == 0)
            {
                var result = (byte[])data.Clone();
                for (int i = 0; i < 65; i++)
                {
                    if (((comb >> i) & UInt128.One) != UInt128.Zero)
                    {
                        result[i / 8] ^= (byte)(1 << (i % 8));
                    }
                }

                return result;
            }

            basis.Add((v, comb));
            basis.Sort((x, y) => y.Vector.CompareTo(x.Vector));
        }

        throw new InvalidOperationException("65 vectors in 64 dimensions are always dependent.");
    }
}

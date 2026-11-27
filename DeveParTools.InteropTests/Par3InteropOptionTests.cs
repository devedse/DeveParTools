using DeveParTools.Par3;

namespace DeveParTools.InteropTests;

/// <summary>
/// par3cmdline as an oracle for the less common PAR3 features: cohorts, XOR, m &gt; k FFT codes, GF(2^16) Cauchy,
/// deduplication, data packets, permissions, comments, first recovery index, moved data.
/// </summary>
[TestClass]
public sealed class Par3InteropOptionTests
{
    private static string Tool()
    {
        var exe = ExternalTool.Find("DEVEPARTOOLS_PAR3CMDLINE", "par3");
        if (exe == null)
        {
            Assert.Inconclusive("par3cmdline not found (set DEVEPARTOOLS_PAR3CMDLINE or put par3 on the PATH).");
        }

        return exe!;
    }

    private sealed class Workspace : IDisposable
    {
        public Workspace(int seed)
        {
            Dir = Path.Combine(Path.GetTempPath(), "deveparttools-interop3-options", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Dir, "sub"));
            var rnd = new Random(seed);
            foreach (var (name, size) in new[] { ("a.bin", 150_000), ("sub/b.bin", 33_333), ("t.bin", 1_100), ("p1.bin", 500), ("p2.bin", 700), ("tiny.bin", 30) })
            {
                var data = new byte[size];
                rnd.NextBytes(data);
                Write(name, data);
            }

            Write("dup.bin", Files["a.bin"]);
        }

        public string Dir { get; }

        public Dictionary<string, byte[]> Files { get; } = new();

        public string[] Names => Files.Keys.Order(StringComparer.Ordinal).ToArray();

        public string this[string name] => Path.Combine(Dir, name);

        public void Write(string name, byte[] data)
        {
            File.WriteAllBytes(this[name], data);
            Files[name] = data;
        }

        public void Corrupt(string name, int offset, int count)
        {
            var data = File.ReadAllBytes(this[name]);
            for (int i = offset; i < offset + count; i++)
            {
                data[i] ^= 0x5A;
            }

            File.WriteAllBytes(this[name], data);
        }

        public void AssertAllRestored()
        {
            foreach (var (name, data) in Files)
            {
                CollectionAssert.AreEqual(data, File.ReadAllBytes(this[name]), name);
            }
        }

        public (int ExitCode, string Output) Run(params string[] args) => ExternalTool.Run(Tool(), Dir, args);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Par3VerifyResult Verify(Workspace w, string par3, params string[] extra)
        => Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = w[par3], ExtraDataFiles = extra.Select(e => w[e]).ToList() });

    private static void WeRepair(Workspace w, string par3, Par3RepairOptions? options = null)
    {
        var verify = Verify(w, par3);
        Assert.IsTrue(verify.RepairPossible, $"missing {verify.MissingBlocks}, recovery {verify.RecoveryBlocksAvailable}");
        var r = Par3Repairer.Repair(verify, options ?? new Par3RepairOptions { PurgeBackups = true });
        Assert.IsTrue(r.Success, r.Message);
        w.AssertAllRestored();
    }

    public static IEnumerable<object[]> TheirCodes()
    {
        yield return ["FFT, 2 cohorts", new[] { "-b40", "-e8", "-i1", "-c12" }];
        yield return ["FFT, 3 cohorts", new[] { "-b40", "-e8", "-i2", "-c9" }];
        yield return ["FFT, GF(2^16)", new[] { "-b300", "-e8", "-c40" }];
        yield return ["Cauchy, GF(2^16)", new[] { "-b300", "-e1", "-c40" }];
        yield return ["Cauchy, first recovery index 5", new[] { "-b40", "-e1", "-cf5", "-c8" }];
        yield return ["deduplication", new[] { "-b40", "-d1", "-c8" }];
    }

    [TestMethod]
    [DynamicData(nameof(TheirCodes))]
    public void TheyCreate_WeRepair(string name, string[] options)
    {
        using var w = new Workspace(1);
        var (rc, output) = w.Run(["c", .. options, "theirs.par3", .. w.Names]);
        Assert.AreEqual(0, rc, $"{name}: par3cmdline create:\n{output}");
        Assert.IsTrue(Verify(w, "theirs.par3").AllFilesComplete, name);

        File.Delete(w["t.bin"]);
        File.Delete(w["p2.bin"]);
        w.Corrupt("a.bin", 20_000, 3000);
        w.Corrupt("dup.bin", 90_000, 10);
        WeRepair(w, "theirs.par3");
    }

    [TestMethod]
    public void TheyCreateXor_WeRepairOneBlock()
    {
        using var w = new Workspace(2);
        var (rc, output) = w.Run(["c", "-b40", "-e8", "-c1", "theirs.par3", .. w.Names]);
        Assert.AreEqual(0, rc, output);
        Assert.AreEqual(0, Verify(w, "theirs.par3").Set.Start.GfSize);
        w.Corrupt("a.bin", 50_000, 1);
        WeRepair(w, "theirs.par3");
    }

    [TestMethod]
    public void TheyCreateMoreRecoveryThanBlocks_WeRepairWithHighRecoveryIndices()
    {
        // 10 input blocks and 20 recovery blocks: m = 32 > k = 10. Without the low recovery volumes, the repair
        // has to use recovery indices ≥ 10.
        using var w = new Workspace(3);
        var (rc, output) = w.Run(["c", "-b10", "-e8", "-c20", "theirs.par3", .. w.Names]);
        Assert.AreEqual(0, rc, output);
        foreach (var first in new[] { "00", "01", "03" })
        {
            File.Delete(Directory.GetFiles(w.Dir, $"theirs.vol{first}+*.par3").Single());
        }

        File.Delete(w["a.bin"]);
        var verify = Verify(w, "theirs.par3");
        Assert.IsTrue(verify.Set.RecoveryBlocks.All(r => r.Index >= 7));
        WeRepair(w, "theirs.par3");
    }

    [TestMethod]
    public void TheyStoreData_WeRestoreEverything()
    {
        using var w = new Workspace(4);
        var (rc, output) = w.Run(["c", "-b40", "-D", "-c2", "theirs.par3", .. w.Names]);
        Assert.AreEqual(0, rc, output);
        foreach (var name in w.Names)
        {
            File.Delete(w[name]);
        }

        Assert.AreEqual(0, Verify(w, "theirs.par3").MissingBlocks);
        WeRepair(w, "theirs.par3");
    }

    [TestMethod]
    public void TheyMove_WeFindBlocksAndTailsByTheirRollingHashes()
    {
        // Moved data is found through the CRC-64 values par3cmdline wrote for blocks and for tails (first 40 bytes).
        using var w = new Workspace(5);
        var (rc, output) = w.Run(["c", "-b40", "-c0", "theirs.par3", .. w.Names]);
        Assert.AreEqual(0, rc, output);
        File.Move(w["t.bin"], w["renamed-t.bin"]);
        File.Move(w["sub/b.bin"], w["renamed-b.bin"]);
        var shifted = (byte[])[1, 2, 3, .. w.Files["a.bin"]];
        File.WriteAllBytes(w["a.bin"], shifted);

        var verify = Verify(w, "theirs.par3", "renamed-t.bin", "renamed-b.bin");
        Assert.AreEqual(0, verify.MissingBlocks);
        Assert.IsTrue(verify.Tails.Values.Any(t => t.Path == w["renamed-t.bin"]));
        Assert.IsTrue(Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }).Success);
        w.AssertAllRestored();
    }

    [TestMethod]
    public void TheyStorePermissionsAndComment_WeReadThem()
    {
        using var w = new Workspace(6);
        var mtime = new DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(w["t.bin"], mtime);
        var (rc, output) = w.Run(["c", "-b40", "-c4", "-fu", "-ff", "-CA comment", "theirs.par3", .. w.Names]);
        Assert.AreEqual(0, rc, output);
        var verify = Verify(w, "theirs.par3");
        Assert.AreEqual("A comment", verify.Set.Comments.Single());
        StringAssert.StartsWith(verify.Set.Creators.Single(), "par3cmdline");

        // par3cmdline writes the mtime in seconds (the spec says nanoseconds) and FILETIME in the FAT packet.
        File.Delete(w["t.bin"]);
        WeRepair(w, "theirs.par3", new Par3RepairOptions { PurgeBackups = true, RestorePermissions = true });
        Assert.AreEqual(mtime, File.GetLastWriteTimeUtc(w["t.bin"]));
    }

    [TestMethod]
    public void WeCreate_TheyFindMovedBlocksAndTails()
    {
        // par3cmdline locates moved data with the CRC-64 values we wrote for blocks and tails.
        using var w = new Workspace(8);
        _ = Tool();
        Par3Creator.Create(new Par3CreateOptions { Inputs = w.Names.Select(n => w[n]).ToList(), OutputPath = w["ours.par3"], BlockCount = 40, RecoveryBlockCount = 0 });
        File.Move(w["t.bin"], w["renamed-t.bin"]);
        File.Move(w["sub/b.bin"], w["renamed-b.bin"]);
        var (rc, output) = w.Run("r", "ours.par3", "renamed-t.bin", "renamed-b.bin");
        Assert.AreEqual(0, rc, output);
        w.AssertAllRestored();
    }

    public static IEnumerable<object[]> OurCodes()
    {
        yield return ["FFT, 2 cohorts", new Par3CreateOptions { Inputs = [], OutputPath = string.Empty, BlockCount = 40, RecoveryBlockCount = 12, Code = Par3Code.Fft, Interleave = 1 }];
        yield return ["FFT, XOR", new Par3CreateOptions { Inputs = [], OutputPath = string.Empty, BlockCount = 40, RecoveryBlockCount = 1, Code = Par3Code.Fft }];
        yield return ["FFT, m > k", new Par3CreateOptions { Inputs = [], OutputPath = string.Empty, BlockCount = 40, RecoveryBlockCount = 8, MaxRecoveryBlocks = 40, Code = Par3Code.Fft }];
        yield return ["Cauchy, GF(2^16)", new Par3CreateOptions { Inputs = [], OutputPath = string.Empty, BlockCount = 300, RecoveryBlockCount = 30, Code = Par3Code.Cauchy }];
        yield return ["Cauchy, first recovery index 5", new Par3CreateOptions { Inputs = [], OutputPath = string.Empty, BlockCount = 40, RecoveryBlockCount = 8, FirstRecoveryBlock = 5, Code = Par3Code.Cauchy }];
        yield return ["deduplication, no tail packing", new Par3CreateOptions { Inputs = [], OutputPath = string.Empty, BlockCount = 40, RecoveryBlockCount = 8, Dedup = Par3Dedup.Blocks, PackTails = false }];
        yield return ["data packets", new Par3CreateOptions { Inputs = [], OutputPath = string.Empty, BlockCount = 40, RecoveryBlockCount = 8, StoreData = true }];
    }

    [TestMethod]
    [DynamicData(nameof(OurCodes))]
    public void WeCreate_TheyVerifyAndRepair(string name, Par3CreateOptions template)
    {
        using var w = new Workspace(7);
        _ = Tool();
        Par3Creator.Create(new Par3CreateOptions
        {
            Inputs = w.Names.Select(n => w[n]).ToList(),
            OutputPath = w["ours.par3"],
            BlockCount = template.BlockCount,
            RecoveryBlockCount = template.RecoveryBlockCount,
            MaxRecoveryBlocks = template.MaxRecoveryBlocks,
            FirstRecoveryBlock = template.FirstRecoveryBlock,
            Code = template.Code,
            Interleave = template.Interleave,
            Dedup = template.Dedup,
            PackTails = template.PackTails,
            StoreData = template.StoreData,
        });
        var (rc, output) = w.Run("v", "ours.par3");
        Assert.AreEqual(0, rc, $"{name}: par3cmdline verify:\n{output}");

        if (template.RecoveryBlockCount == 1)
        {
            w.Corrupt("a.bin", 50_000, 1);
        }
        else
        {
            File.Delete(w["t.bin"]);
            w.Corrupt("a.bin", 20_000, 3000);
        }

        (rc, output) = w.Run("r", "ours.par3");
        Assert.AreEqual(0, rc, $"{name}: par3cmdline repair:\n{output}");
        w.AssertAllRestored();
    }
}

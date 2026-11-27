using DeveParTools.Core.Operations;
using DeveParTools.Par3;

namespace DeveParTools.Par3.Tests;

[TestClass]
public sealed class Par3RoundTripTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "deveparttools-par3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static Dictionary<string, byte[]> MakeTree(string dir, int seed)
    {
        var rnd = new Random(seed);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[] { ("a.bin", 150_000), ("sub/b.bin", 33_333), ("sub/deep/c.bin", 4096), ("tiny.txt", 10), ("tail.bin", 1100), ("empty.bin", 0) })
        {
            var p = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(p, data);
            files[p] = data;
        }

        Directory.CreateDirectory(Path.Combine(dir, "emptydir"));
        return files;
    }

    [TestMethod]
    [DataRow(Par3Code.Cauchy, Par3Dedup.None)]
    [DataRow(Par3Code.Fft, Par3Dedup.None)]
    [DataRow(Par3Code.Cauchy, Par3Dedup.Blocks)]
    [DataRow(Par3Code.Fft, Par3Dedup.Blocks)]
    public void CreateVerifyRepair(Par3Code code, Par3Dedup dedup)
    {
        var dir = NewDir();
        var files = MakeTree(dir, 1);
        // Duplicate content to exercise dedup.
        File.WriteAllBytes(Path.Combine(dir, "dup.bin"), files[Path.Combine(dir, "a.bin")]);
        files[Path.Combine(dir, "dup.bin")] = files[Path.Combine(dir, "a.bin")];

        var created = Par3Creator.Create(new Par3CreateOptions
        {
            Inputs = [dir],
            OutputPath = Path.Combine(dir, "set.par3"),
            BlockSize = 4096,
            RedundancyPercent = 40,
            Code = code,
            Dedup = dedup,
        });
        Assert.IsTrue(created.RecoveryBlocks > 0);

        var ok = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = Path.Combine(dir, "set.par3") });
        Assert.IsTrue(ok.AllFilesComplete, string.Join(", ", ok.Files.Where(f => f.Status != Par3FileStatus.Complete).Select(f => f.File.RelativePath + ":" + f.Status)));

        // Damage: delete, corrupt, shift, remove empty dir.
        File.Delete(Path.Combine(dir, "sub", "deep", "c.bin"));
        File.Delete(Path.Combine(dir, "tail.bin"));
        var a = File.ReadAllBytes(Path.Combine(dir, "a.bin"));
        a[70_000] ^= 0x55;
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), a);
        var b = File.ReadAllBytes(Path.Combine(dir, "sub", "b.bin")).ToList();
        b.InsertRange(100, new byte[] { 9, 9, 9, 9, 9 });
        File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), b.ToArray());
        Directory.Delete(Path.Combine(dir, "emptydir"));

        var ctx = new OperationContext();
        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = Path.Combine(dir, "set.par3") }, ctx);
        Assert.IsFalse(verify.AllFilesComplete);
        Assert.IsTrue(verify.RepairPossible, $"missing {verify.MissingBlocks}, recovery {verify.RecoveryBlocksAvailable}");
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true }, ctx);
        Assert.IsTrue(repair.Success, repair.Message);
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }

        Assert.IsTrue(Directory.Exists(Path.Combine(dir, "emptydir")));
        var again = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = Path.Combine(dir, "set.par3") });
        Assert.IsTrue(again.AllFilesComplete);
    }

    [TestMethod]
    public void StoreData_ArchiveModeRecoversEverything()
    {
        var dir = NewDir();
        var files = MakeTree(dir, 2);
        Par3Creator.Create(new Par3CreateOptions { Inputs = [dir], OutputPath = Path.Combine(dir, "arch.par3"), BlockSize = 2048, RecoveryBlockCount = 2, StoreData = true });
        foreach (var f in files.Keys)
        {
            File.Delete(f);
        }

        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = Path.Combine(dir, "arch.par3") });
        Assert.AreEqual(0, verify.MissingBlocks);
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true });
        Assert.IsTrue(repair.Success, repair.Message);
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }
}

using DeveParTools.Core.Operations;
using DeveParTools.Par2;

namespace DeveParTools.Par2.Tests;

[TestClass]
public sealed class Par2RoundTripTests
{
    public TestContext TestContext { get; set; } = null!;

    private string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "deveparttools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [TestMethod]
    public void CreateVerifyRepair_DeletedAndCorruptedFiles()
    {
        var dir = NewDir();
        var rnd = new Random(1);
        var files = new List<string>();
        foreach (var (name, size) in new[] { ("a.bin", 100_000), ("sub/b.bin", 33_333), ("c.bin", 4096), ("empty.bin", 0) })
        {
            var p = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(p, data);
            files.Add(p);
        }

        var originals = files.ToDictionary(f => f, File.ReadAllBytes);
        var result = Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = files,
            OutputPath = Path.Combine(dir, "set.par2"),
            SliceSize = 4096,
            RedundancyPercent = 30,
        });
        Assert.IsTrue(result.RecoverySlices > 0);

        var ok = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = Path.Combine(dir, "set.par2") });
        Assert.IsTrue(ok.AllFilesComplete, string.Join(",", ok.Files.Select(f => f.File.Name + ":" + f.Status)));

        // Damage: delete c.bin, corrupt a.bin in the middle, insert bytes into b.bin (shifts data).
        File.Delete(Path.Combine(dir, "c.bin"));
        var a = File.ReadAllBytes(Path.Combine(dir, "a.bin"));
        a[50_000] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), a);
        var b = File.ReadAllBytes(Path.Combine(dir, "sub/b.bin")).ToList();
        b.InsertRange(5000, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(dir, "sub/b.bin"), b.ToArray());

        var ctx = new OperationContext();
        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = Path.Combine(dir, "set.par2") }, ctx);
        Assert.IsFalse(verify.AllFilesComplete);
        Assert.IsTrue(verify.RepairPossible);
        var counts = ctx.Blocks!.CountStates();
        Assert.IsTrue(counts[(int)BlockState.FoundMoved] > 0, "shifted slices of b.bin must be found");

        var repair = Par2Repairer.Repair(verify, new Par2RepairOptions { PurgeBackups = true }, ctx);
        Assert.IsTrue(repair.Success, repair.Message);
        foreach (var (path, data) in originals)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }
}

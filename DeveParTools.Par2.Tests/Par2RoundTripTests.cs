using DeveParTools.Core.Operations;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests;

[TestClass]
public sealed class Par2RoundTripTests
{
    [TestMethod]
    public void CreateVerifyRepair_DeletedAndCorruptedFiles()
    {
        using var dir = new TempDir();
        var originals = TestData.WriteFiles(dir, 1, ("a.bin", 100_000), ("sub/b.bin", 33_333), ("c.bin", 4096), ("empty.bin", 0));
        var result = Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = originals.Keys.ToList(),
            OutputPath = dir["set.par2"],
            SliceSize = 4096,
            RedundancyPercent = 30,
        });
        Assert.IsTrue(result.RecoverySlices > 0);

        var ok = TestData.Verify(dir);
        Assert.IsTrue(ok.AllFilesComplete, string.Join(",", ok.Files.Select(f => f.File.Name + ":" + f.Status)));

        // Damage: delete c.bin, corrupt a.bin in the middle, insert bytes into b.bin (shifts data).
        File.Delete(dir["c.bin"]);
        TestData.Corrupt(dir["a.bin"], 50_000);
        var b = dir.Read("sub/b.bin").ToList();
        b.InsertRange(5000, new byte[] { 1, 2, 3 });
        dir.Write("sub/b.bin", b.ToArray());

        var ctx = new OperationContext();
        var verify = TestData.Verify(dir, context: ctx);
        Assert.IsFalse(verify.AllFilesComplete);
        Assert.IsTrue(verify.RepairPossible);
        var counts = ctx.Blocks!.CountStates();
        Assert.IsTrue(counts[(int)BlockState.FoundMoved] > 0, "shifted slices of b.bin must be found");

        var repair = Par2Repairer.Repair(verify, new Par2RepairOptions { PurgeBackups = true }, ctx);
        Assert.IsTrue(repair.Success, repair.Message);
        TestData.AssertFiles(originals);
    }
}

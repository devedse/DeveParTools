using DeveParTools.Par1.Tests.TestSupport;

namespace DeveParTools.Par1.Tests;

[TestClass]
public sealed class Par1RoundTripTests
{
    [TestMethod]
    public void CreateVerifyRepair()
    {
        using var dir = new TempDir();
        var rnd = new Random(3);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[] { ("one.d01", 5000), ("two.d02", 12000), ("three.d03", 7), ("four.d04", 0) })
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            files[dir.Write(name, data)] = data;
        }

        var written = Par1Set.Create(new Par1CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = dir["set.par"], RecoveryVolumes = 3, Comment = "hello" });
        Assert.HasCount(4, written);
        StringAssert.EndsWith(written[3], ".p03");

        var ok = Par1Set.Verify(dir["set.par"]);
        Assert.IsTrue(ok.AllFilesComplete);
        Assert.HasCount(3, ok.Volumes);
        Assert.AreEqual("hello", ok.Index.Comment);

        File.Delete(dir["one.d01"]);
        var two = dir.Read("two.d02");
        two[100] ^= 1;
        dir.Write("two.d02", two);
        File.Move(dir["three.d03"], dir["renamed.bin"]);

        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(2, verify.MissingSavedFiles);
        Assert.IsNotNull(verify.Files.Single(f => f.Entry.Name == "three.d03").FoundAs);
        Assert.IsTrue(Par1Set.Repair(verify));
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }
}

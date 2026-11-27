using DeveParTools.Par1;

namespace DeveParTools.Par1.Tests;

[TestClass]
public sealed class Par1Tests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "deveparttools-par1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [TestMethod]
    public void CreateVerifyRepair()
    {
        var dir = NewDir();
        var rnd = new Random(3);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[] { ("one.d01", 5000), ("two.d02", 12000), ("three.d03", 7), ("four.d04", 0) })
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            files[Path.Combine(dir, name)] = data;
        }

        var written = Par1Set.Create(new Par1CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = Path.Combine(dir, "set.par"), RecoveryVolumes = 3, Comment = "hello" });
        Assert.AreEqual(4, written.Count);
        StringAssert.EndsWith(written[3], ".p03");

        var ok = Par1Set.Verify(Path.Combine(dir, "set.par"));
        Assert.IsTrue(ok.AllFilesComplete);
        Assert.AreEqual(3, ok.Volumes.Count);
        Assert.AreEqual("hello", ok.Index.Comment);

        File.Delete(Path.Combine(dir, "one.d01"));
        var two = File.ReadAllBytes(Path.Combine(dir, "two.d02"));
        two[100] ^= 1;
        File.WriteAllBytes(Path.Combine(dir, "two.d02"), two);
        File.Move(Path.Combine(dir, "three.d03"), Path.Combine(dir, "renamed.bin"));

        var verify = Par1Set.Verify(Path.Combine(dir, "set.par"));
        Assert.AreEqual(2, verify.MissingSavedFiles);
        Assert.IsNotNull(verify.Files.Single(f => f.Entry.Name == "three.d03").FoundAs);
        Assert.IsTrue(Par1Set.Repair(verify));
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    [TestMethod]
    public void VolumeExtensions()
    {
        Assert.AreEqual(".par", Par1File.VolumeExtension(0));
        Assert.AreEqual(".p01", Par1File.VolumeExtension(1));
        Assert.AreEqual(".p99", Par1File.VolumeExtension(99));
        Assert.AreEqual(".q00", Par1File.VolumeExtension(100));
    }

    [TestMethod]
    public void CoefficientsFollowPlank()
    {
        Assert.AreEqual((byte)1, Par1Math.Coefficient(5, 1));
        Assert.AreEqual((byte)3, Par1Math.Coefficient(2, 2));
        Assert.AreEqual(Par1Math.Field.Mul(3, 3), Par1Math.Coefficient(2, 3));
    }
}

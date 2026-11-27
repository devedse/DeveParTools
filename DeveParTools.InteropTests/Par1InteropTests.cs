using DeveParTools.Par1;

namespace DeveParTools.InteropTests;

/// <summary>Our PAR1 sets, verified and repaired by par2cmdline (which reads PAR1 too).</summary>
[TestClass]
public sealed class Par1InteropTests
{
    private const int Success = 0;
    private const int RepairPossible = 1;

    private static string FindTool()
    {
        var exe = ExternalTool.Find("DEVEPARTOOLS_PAR2CMDLINE", "par2");
        if (exe == null)
        {
            Assert.Inconclusive("par2cmdline not found.");
        }

        return exe;
    }

    private static (string Dir, Dictionary<string, byte[]> Files) MakeSet(int seed, int volumes, string? comment, params (string Name, int Size)[] spec)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deveparttools-interop1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rnd = new Random(seed);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in spec)
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            files[Path.Combine(dir, name)] = data;
        }

        Par1Set.Create(new Par1CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = Path.Combine(dir, "set.par"), RecoveryVolumes = volumes, Comment = comment });
        return (dir, files);
    }

    private static void AssertFiles(Dictionary<string, byte[]> files)
    {
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    [TestMethod]
    public void WeCreate_Par2cmdlineRepairs()
    {
        var exe = FindTool();
        var (dir, files) = MakeSet(9, 2, null, ("a.r01", 30000), ("b.r02", 20000), ("c.r03", 25000));
        try
        {
            File.Delete(Path.Combine(dir, "b.r02"));
            var (rc, output) = ExternalTool.Run(exe, dir, "r", "set.par");
            Assert.AreEqual(Success, rc, output);
            AssertFiles(files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void WeCreate_WithCommentAndManyVolumes_Par2cmdlineVerifiesAndRepairsTwoFiles()
    {
        var exe = FindTool();
        var (dir, files) = MakeSet(10, 4, "a PAR1 comment", ("one.bin", 5000), ("two.bin", 12000), ("three.bin", 7), ("four.bin", 999), ("five.bin", 16385));
        try
        {
            var (rc, output) = ExternalTool.Run(exe, dir, "v", "set.par");
            Assert.AreEqual(Success, rc, output);

            File.Delete(Path.Combine(dir, "two.bin"));
            var five = File.ReadAllBytes(Path.Combine(dir, "five.bin"));
            five[16384] ^= 1;
            File.WriteAllBytes(Path.Combine(dir, "five.bin"), five);
            File.Delete(Path.Combine(dir, "set.p01"));
            (rc, output) = ExternalTool.Run(exe, dir, "v", "set.par");
            Assert.AreEqual(RepairPossible, rc, output);

            // Our verifier agrees, and par2cmdline repairs with volumes 2..4.
            var verify = Par1Set.Verify(Path.Combine(dir, "set.par"));
            Assert.AreEqual(2, verify.MissingSavedFiles);
            Assert.IsTrue(verify.RepairPossible);
            (rc, output) = ExternalTool.Run(exe, dir, "r", "set.par");
            Assert.AreEqual(Success, rc, output);
            AssertFiles(files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

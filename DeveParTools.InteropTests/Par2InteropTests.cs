using DeveParTools.Par2;

namespace DeveParTools.InteropTests;

[TestClass]
public sealed class Par2InteropTests
{
    public static IEnumerable<object[]> Tools()
    {
        var reference = ExternalTool.Find("DEVEPARTOOLS_PAR2CMDLINE", "par2");
        if (reference != null)
        {
            yield return ["par2cmdline", reference];
        }

        var turbo = ExternalTool.Find("DEVEPARTOOLS_PAR2TURBO");
        if (turbo != null)
        {
            yield return ["par2cmdline-turbo", turbo];
        }
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "deveparttools-interop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static Dictionary<string, byte[]> MakeFiles(string dir, int seed)
    {
        var rnd = new Random(seed);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[] { ("one.bin", 250_000), ("two.dat", 77_777), ("three.txt", 1_000) })
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            files[Path.Combine(dir, name)] = data;
        }

        return files;
    }

    private static void Damage(string dir)
    {
        File.Delete(Path.Combine(dir, "three.txt"));
        var one = File.ReadAllBytes(Path.Combine(dir, "one.bin"));
        for (int i = 10_000; i < 30_000; i++)
        {
            one[i] = 0;
        }

        File.WriteAllBytes(Path.Combine(dir, "one.bin"), one);
    }

    [TestMethod]
    [DynamicData(nameof(Tools))]
    public void WeCreate_TheyVerifyAndRepair(string toolName, string exe)
    {
        var dir = NewDir();
        var files = MakeFiles(dir, 1);
        Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = files.Keys.ToList(),
            OutputPath = Path.Combine(dir, "set.par2"),
            SliceSize = 8192,
            RedundancyPercent = 25,
            Comment = "interop test",
        });

        var (code, output) = ExternalTool.Run(exe, dir, "v", "set.par2");
        Assert.AreEqual(0, code, $"{toolName} verify failed:\n{output}");

        Damage(dir);
        (code, output) = ExternalTool.Run(exe, dir, "r", "set.par2");
        Assert.AreEqual(0, code, $"{toolName} repair failed:\n{output}");
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Tools))]
    public void TheyCreate_WeVerifyAndRepair(string toolName, string exe)
    {
        var dir = NewDir();
        var files = MakeFiles(dir, 2);
        var (code, output) = ExternalTool.Run(exe, dir, ["c", "-s8192", "-r25", "set.par2", .. files.Keys.Select(Path.GetFileName)!]);
        Assert.AreEqual(0, code, $"{toolName} create failed:\n{output}");

        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = Path.Combine(dir, "set.par2") });
        Assert.IsTrue(verify.AllFilesComplete);

        Damage(dir);
        verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = Path.Combine(dir, "set.par2") });
        Assert.IsTrue(verify.RepairPossible);
        var repair = Par2Repairer.Repair(verify);
        Assert.IsTrue(repair.Success, repair.Message);
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }
}

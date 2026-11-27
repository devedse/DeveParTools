using DeveParTools.Par3;

namespace DeveParTools.InteropTests;

[TestClass]
public sealed class Par3InteropTests
{
    public static IEnumerable<object[]> Codes()
    {
        var exe = ExternalTool.Find("DEVEPARTOOLS_PAR3CMDLINE", "par3");
        if (exe == null)
        {
            yield return [string.Empty, Par3Code.Cauchy, "-e1"];
            yield break;
        }

        yield return [exe, Par3Code.Cauchy, "-e1"];
        yield return [exe, Par3Code.Fft, "-e8"];
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "deveparttools-interop3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        return d;
    }

    private static Dictionary<string, byte[]> MakeFiles(string dir, int seed)
    {
        var rnd = new Random(seed);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[] { ("a.bin", 150_000), ("sub/b.bin", 33_333), ("t.bin", 1_100) })
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, data);
            files[p] = data;
        }

        return files;
    }

    private static void Damage(string dir)
    {
        File.Delete(Path.Combine(dir, "t.bin"));
        var a = File.ReadAllBytes(Path.Combine(dir, "a.bin"));
        for (int i = 20_000; i < 26_000; i++)
        {
            a[i] ^= 0xAA;
        }

        File.WriteAllBytes(Path.Combine(dir, "a.bin"), a);
    }

    [TestMethod]
    [DynamicData(nameof(Codes))]
    public void WeCreate_TheyVerifyAndRepair(string exe, Par3Code code, string flag)
    {
        if (exe.Length == 0)
        {
            Assert.Inconclusive("par3cmdline not found (set DEVEPARTOOLS_PAR3CMDLINE or put par3 on the PATH).");
        }

        var dir = NewDir();
        var files = MakeFiles(dir, 1);
        Par3Creator.Create(new Par3CreateOptions { Inputs = files.Keys.ToList(), OutputPath = Path.Combine(dir, "ours.par3"), BlockCount = 40, RecoveryBlockCount = 12, Code = code });

        var (rc, output) = ExternalTool.Run(exe, dir, "v", "ours.par3");
        Assert.AreEqual(0, rc, $"par3cmdline verify ({flag}):\n{output}");

        Damage(dir);
        (rc, output) = ExternalTool.Run(exe, dir, "r", "ours.par3");
        Assert.AreEqual(0, rc, $"par3cmdline repair ({flag}):\n{output}");
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Codes))]
    public void TheyCreate_WeVerifyAndRepair(string exe, Par3Code code, string flag)
    {
        if (exe.Length == 0)
        {
            Assert.Inconclusive("par3cmdline not found (set DEVEPARTOOLS_PAR3CMDLINE or put par3 on the PATH).");
        }

        var dir = NewDir();
        var files = MakeFiles(dir, 2);
        var (rc, output) = ExternalTool.Run(exe, dir, "c", "-b40", "-c12", flag, "theirs.par3", "a.bin", "sub/b.bin", "t.bin");
        Assert.AreEqual(0, rc, $"par3cmdline create ({flag}):\n{output}");

        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = Path.Combine(dir, "theirs.par3") });
        Assert.IsTrue(verify.AllFilesComplete, $"{code}: " + string.Join(", ", verify.Files.Select(f => f.File.RelativePath + ":" + f.Status)));

        Damage(dir);
        verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = Path.Combine(dir, "theirs.par3") });
        Assert.IsTrue(verify.RepairPossible);
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true });
        Assert.IsTrue(repair.Success, repair.Message);
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }
}

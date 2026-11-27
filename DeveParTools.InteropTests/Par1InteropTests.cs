using DeveParTools.Par1;

namespace DeveParTools.InteropTests;

[TestClass]
public sealed class Par1InteropTests
{
    [TestMethod]
    public void WeCreate_Par2cmdlineRepairs()
    {
        var exe = ExternalTool.Find("DEVEPARTOOLS_PAR2CMDLINE", "par2");
        if (exe == null)
        {
            Assert.Inconclusive("par2cmdline not found.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "deveparttools-interop1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rnd = new Random(9);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[] { ("a.r01", 30000), ("b.r02", 20000), ("c.r03", 25000) })
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            files[Path.Combine(dir, name)] = data;
        }

        Par1Set.Create(new Par1CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = Path.Combine(dir, "set.par"), RecoveryVolumes = 2 });
        File.Delete(Path.Combine(dir, "b.r02"));
        var (rc, output) = ExternalTool.Run(exe, dir, "r", "set.par");
        Assert.AreEqual(0, rc, output);
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }
}

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

    /// <summary>
    /// par3cmdline creates a set, we add recovery blocks, and par3cmdline repairs with only our new blocks
    /// (its own recovery files are deleted first). This checks that extended blocks are compatible.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Codes))]
    public void TheyCreate_WeExtend_TheyRepairWithOurBlocks(string exe, Par3Code code, string flag)
    {
        if (exe.Length == 0)
        {
            Assert.Inconclusive("par3cmdline not found (set DEVEPARTOOLS_PAR3CMDLINE or put par3 on the PATH).");
        }

        var dir = NewDir();
        var files = MakeFiles(dir, 3);
        var (rc, output) = ExternalTool.Run(exe, dir, "c", "-b40", "-c4", "-cm32", flag, "theirs.par3", "a.bin", "sub/b.bin", "t.bin");
        Assert.AreEqual(0, rc, $"par3cmdline create ({flag}):\n{output}");
        var theirVolumes = Directory.GetFiles(dir, "theirs.vol*.par3");

        var extended = Par3Extender.Extend(new Par3ExtendOptions { Par3Path = Path.Combine(dir, "theirs.par3"), RecoveryBlockCount = 12 });
        Assert.AreEqual(12, extended.RecoveryBlocks, code.ToString());
        foreach (var v in theirVolumes)
        {
            File.Delete(v);
        }

        Damage(dir);
        (rc, output) = ExternalTool.Run(exe, dir, "r", "theirs.par3");
        Assert.AreEqual(0, rc, $"par3cmdline repair with extended blocks ({flag}):\n{output}");
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    public static IEnumerable<object[]> Archives()
    {
        var exe = ExternalTool.Find("DEVEPARTOOLS_PAR3CMDLINE", "par3") ?? string.Empty;
        yield return [exe, "archive.zip"];
        if (exe.Length > 0)
        {
            yield return [exe, "archive.7z"];
        }
    }

    private static byte[] MakeArchive(string name, int seed)
    {
        if (name.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var rnd = new Random(seed);
                for (int i = 0; i < 4; i++)
                {
                    var data = new byte[40_000 + i * 1000];
                    rnd.NextBytes(data);
                    using var s = zip.CreateEntry($"f{i}.bin", System.IO.Compression.CompressionLevel.NoCompression).Open();
                    s.Write(data);
                }
            }

            return ms.ToArray();
        }

        // Minimal 7z layout: start header, packed data, next header ending with kEnd.
        var packed = new byte[60_000];
        new Random(seed).NextBytes(packed);
        var file = new byte[32 + packed.Length + 12];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(file, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(12), packed.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(20), 12);
        packed.CopyTo(file, 32);
        return file;
    }

    private static void DamageArchive(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (int i = 1000; i < 1300; i++)
        {
            bytes[i] ^= 0x5A;
        }

        File.WriteAllBytes(path, bytes);
    }

    [TestMethod]
    [DynamicData(nameof(Archives))]
    public void Inside_WeInsert_TheyVerifyRepairAndDelete(string exe, string name)
    {
        if (exe.Length == 0)
        {
            Assert.Inconclusive("par3cmdline not found (set DEVEPARTOOLS_PAR3CMDLINE or put par3 on the PATH).");
        }

        var dir = NewDir();
        var path = Path.Combine(dir, name);
        var original = MakeArchive(name, 4);
        File.WriteAllBytes(path, original);
        Par3Inside.Insert(new Par3InsideOptions { ArchivePath = path, RedundancyPercent = 5 });

        var (rc, output) = ExternalTool.Run(exe, dir, "vs", name);
        Assert.AreEqual(0, rc, $"par3cmdline verify itself:\n{output}");
        DamageArchive(path);
        (rc, output) = ExternalTool.Run(exe, dir, "rs", name);
        Assert.AreEqual(0, rc, $"par3cmdline repair itself:\n{output}");
        (rc, output) = ExternalTool.Run(exe, dir, "d", name);
        Assert.AreEqual(0, rc, $"par3cmdline delete:\n{output}");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DynamicData(nameof(Archives))]
    public void Inside_TheyInsert_WeVerifyRepairAndDelete(string exe, string name)
    {
        if (exe.Length == 0)
        {
            Assert.Inconclusive("par3cmdline not found (set DEVEPARTOOLS_PAR3CMDLINE or put par3 on the PATH).");
        }

        var dir = NewDir();
        var path = Path.Combine(dir, name);
        var original = MakeArchive(name, 5);
        File.WriteAllBytes(path, original);
        var (rc, output) = ExternalTool.Run(exe, dir, "i", "-r5", name);
        Assert.AreEqual(0, rc, $"par3cmdline insert:\n{output}");

        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = path });
        Assert.AreEqual(Par3FileStatus.Complete, verify.Files.Single().Status);
        DamageArchive(path);
        verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = path });
        Assert.AreEqual(Par3FileStatus.Damaged, verify.Files.Single().Status);
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true });
        Assert.IsTrue(repair.Success, repair.Message);
        Assert.AreEqual(original.Length, Par3Inside.Delete(path));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }
}

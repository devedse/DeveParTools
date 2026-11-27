using System.Globalization;
using System.Text.RegularExpressions;
using DeveParTools.Core.Operations;
using DeveParTools.Par2;

namespace DeveParTools.InteropTests;

/// <summary>
/// PAR2 interoperability with par2cmdline and par2cmdline-turbo: sets created by one side are verified and repaired
/// by the other, and both sides agree on whether a damaged set is repairable.
/// </summary>
[TestClass]
public sealed class Par2InteropTests
{
    // par2cmdline exit codes.
    private const int Success = 0;
    private const int RepairPossible = 1;
    private const int RepairNotPossible = 2;

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

        if (reference == null && turbo == null)
        {
            yield return ["(none)", string.Empty];
        }
    }

    /// <summary>The volume schemes and encoding variants of our creator, each checked by par2cmdline.</summary>
    public static IEnumerable<object[]> CreateVariants()
    {
        var reference = ExternalTool.Find("DEVEPARTOOLS_PAR2CMDLINE", "par2") ?? string.Empty;
        yield return ["exponential", reference];
        yield return ["exponential-2-volumes", reference];
        yield return ["uniform-3-volumes", reference];
        yield return ["limit-size", reference];
        yield return ["first-exponent-1000", reference];
        yield return ["multi-pass", reference];
        yield return ["slice-count", reference];
    }

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Dir = Path.Combine(Path.GetTempPath(), "deveparttools-interop", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; }

        public string this[string name] => Path.Combine(Dir, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    private static Dictionary<string, byte[]> MakeFiles(Workspace ws, int seed, params (string Name, int Size)[] spec)
    {
        var rnd = new Random(seed);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in spec.Length > 0 ? spec : [("one.bin", 250_000), ("two.dat", 77_777), ("three.txt", 1_000)])
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            var path = ws[name];
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            files[path] = data;
        }

        return files;
    }

    private static void Damage(Workspace ws)
    {
        File.Delete(ws["three.txt"]);
        var one = File.ReadAllBytes(ws["one.bin"]);
        for (int i = 10_000; i < 30_000; i++)
        {
            one[i] = 0;
        }

        File.WriteAllBytes(ws["one.bin"], one);
    }

    private static void AssertFiles(Dictionary<string, byte[]> files)
    {
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    private static void SkipWithoutTool(string exe)
    {
        if (exe.Length == 0)
        {
            Assert.Inconclusive("par2cmdline not found (set DEVEPARTOOLS_PAR2CMDLINE or put par2 on the PATH).");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Tools))]
    public void WeCreate_TheyVerifyAndRepair(string toolName, string exe)
    {
        SkipWithoutTool(exe);
        using var ws = new Workspace();
        var files = MakeFiles(ws, 1);
        Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = files.Keys.ToList(),
            OutputPath = ws["set.par2"],
            SliceSize = 8192,
            RedundancyPercent = 25,
            Comment = "interop test",
        });

        var (code, output) = ExternalTool.Run(exe, ws.Dir, "v", "set.par2");
        Assert.AreEqual(Success, code, $"{toolName} verify failed:\n{output}");

        Damage(ws);
        (code, output) = ExternalTool.Run(exe, ws.Dir, "r", "set.par2");
        Assert.AreEqual(Success, code, $"{toolName} repair failed:\n{output}");
        AssertFiles(files);
    }

    [TestMethod]
    [DynamicData(nameof(Tools))]
    public void TheyCreate_WeVerifyAndRepair(string toolName, string exe)
    {
        SkipWithoutTool(exe);
        using var ws = new Workspace();
        var files = MakeFiles(ws, 2);
        var (code, output) = ExternalTool.Run(exe, ws.Dir, ["c", "-s8192", "-r25", "set.par2", .. files.Keys.Select(Path.GetFileName)!]);
        Assert.AreEqual(Success, code, $"{toolName} create failed:\n{output}");

        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = ws["set.par2"] });
        Assert.IsTrue(verify.AllFilesComplete);

        Damage(ws);
        verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = ws["set.par2"] });
        Assert.IsTrue(verify.RepairPossible);
        var repair = Par2Repairer.Repair(verify);
        Assert.IsTrue(repair.Success, repair.Message);
        AssertFiles(files);
    }

    [TestMethod]
    [DynamicData(nameof(CreateVariants))]
    public void EveryCreateVariant_IsAcceptedAndRepairedByPar2cmdline(string variant, string exe)
    {
        SkipWithoutTool(exe);
        using var ws = new Workspace();
        var files = MakeFiles(ws, 3, ("one.bin", 250_000), ("sub/two.dat", 77_777), ("three.txt", 1_000), ("empty", 0));
        var options = new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = ws["set.par2"], SliceSize = 4096, RecoverySliceCount = 20 };
        var context = new OperationContext();
        options = variant switch
        {
            "exponential" => options,
            "exponential-2-volumes" => new Par2CreateOptions { InputFiles = options.InputFiles, OutputPath = options.OutputPath, SliceSize = 4096, RecoverySliceCount = 20, VolumeCount = 2 },
            "uniform-3-volumes" => new Par2CreateOptions { InputFiles = options.InputFiles, OutputPath = options.OutputPath, SliceSize = 4096, RecoverySliceCount = 20, Scheme = Par2VolumeScheme.Uniform, VolumeCount = 3 },
            "limit-size" => new Par2CreateOptions { InputFiles = options.InputFiles, OutputPath = options.OutputPath, SliceSize = 65536, RecoverySliceCount = 12, Scheme = Par2VolumeScheme.LimitSize },
            "first-exponent-1000" => new Par2CreateOptions { InputFiles = options.InputFiles, OutputPath = options.OutputPath, SliceSize = 4096, RecoverySliceCount = 20, FirstExponent = 1000 },
            "slice-count" => new Par2CreateOptions { InputFiles = options.InputFiles, OutputPath = options.OutputPath, SliceCount = 37, RecoverySliceCount = 8 },
            _ => options,
        };
        if (variant == "multi-pass")
        {
            context = new OperationContext { MemoryBudget = 1 };
        }

        var created = Par2Creator.Create(options, context);
        var (code, output) = ExternalTool.Run(exe, ws.Dir, "v", "set.par2");
        Assert.AreEqual(Success, code, $"{variant}: verify failed:\n{output}");
        int loaded = Regex.Matches(output, @"including (\d+) recovery blocks").Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.AreEqual(created.RecoverySlices, loaded, $"{variant}: every recovery packet is accepted\n{output}");

        // Damage that needs all but one recovery slice ... and par2cmdline repairs it.
        File.Delete(ws["three.txt"]);
        var one = File.ReadAllBytes(ws["one.bin"]);
        int damageSlices = created.RecoverySlices - 2;
        Array.Clear(one, 0, (int)Math.Min(one.Length, damageSlices * created.SliceSize));
        File.WriteAllBytes(ws["one.bin"], one);
        (code, output) = ExternalTool.Run(exe, ws.Dir, "r", "set.par2");
        Assert.AreEqual(Success, code, $"{variant}: repair failed:\n{output}");
        AssertFiles(files);
    }

    [TestMethod]
    [DataRow("-n3", DisplayName = "exponential, 3 files")]
    [DataRow("-u", DisplayName = "uniform")]
    [DataRow("-l", DisplayName = "limited size")]
    [DataRow("-f777", DisplayName = "first block 777")]
    [DataRow("-b150", DisplayName = "block count 150")]
    public void TheyCreateWithOptions_WeVerifyAndRepair(string option)
    {
        var exe = ExternalTool.Find("DEVEPARTOOLS_PAR2CMDLINE", "par2") ?? string.Empty;
        SkipWithoutTool(exe);
        using var ws = new Workspace();
        var files = MakeFiles(ws, 4, ("one.bin", 250_000), ("sub/two.dat", 77_777), ("three.txt", 1_000));
        string[] sizeArgs = option.StartsWith("-b", StringComparison.Ordinal) ? [option] : ["-s4096", option];
        var (code, output) = ExternalTool.Run(exe, ws.Dir, ["c", .. sizeArgs, "-c30", "set.par2", "one.bin", "sub/two.dat", "three.txt"]);
        Assert.AreEqual(Success, code, output);

        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = ws["set.par2"] });
        Assert.IsTrue(verify.AllFilesComplete);
        Assert.AreEqual(30, verify.RecoverySlicesAvailable);
        Assert.AreEqual("sub/two.dat", verify.Files.Single(f => f.File.Length == 77_777).File.Name);

        File.Delete(ws["sub/two.dat"]);
        File.Delete(ws["three.txt"]);
        verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = ws["set.par2"] });
        Assert.AreEqual(verify.MissingSlices <= 30, verify.RepairPossible);
        if (verify.RepairPossible)
        {
            Assert.IsTrue(Par2Repairer.Repair(verify).Success);
            AssertFiles(files);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Tools))]
    public void BothSidesAgree_OnWhetherRepairIsPossible(string toolName, string exe)
    {
        SkipWithoutTool(exe);
        using var ws = new Workspace();
        var files = MakeFiles(ws, 5);
        Par2Creator.Create(new Par2CreateOptions { InputFiles = files.Keys.ToList(), OutputPath = ws["set.par2"], SliceSize = 4096, RecoverySliceCount = 10 });

        // Slices 24..33 zeroed and everything shifted by 2 bytes: 10 slices missing, the rest found by both.
        var one = File.ReadAllBytes(ws["one.bin"]);
        File.WriteAllBytes(ws["one.bin"], [9, 9, .. one.AsSpan(0, 24 * 4096), .. new byte[10 * 4096], .. one.AsSpan(34 * 4096)]);
        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = ws["set.par2"] });
        Assert.AreEqual(10, verify.MissingSlices);
        Assert.IsTrue(verify.RepairPossible);
        var (code, output) = ExternalTool.Run(exe, ws.Dir, "v", "set.par2");
        Assert.AreEqual(RepairPossible, code, $"{toolName}:\n{output}");

        // One more missing slice: not possible for either.
        File.WriteAllBytes(ws["three.txt"], [0]);
        verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = ws["set.par2"] });
        Assert.AreEqual(11, verify.MissingSlices);
        Assert.IsFalse(verify.RepairPossible);
        (code, output) = ExternalTool.Run(exe, ws.Dir, "v", "set.par2");
        Assert.AreEqual(RepairNotPossible, code, $"{toolName}:\n{output}");
    }
}

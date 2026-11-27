using DeveParTools.Core.Operations;
using DeveParTools.Par1;
using DeveParTools.Par2;
using DeveParTools.Par3;

namespace DeveParTools.Tests;

[TestClass]
public sealed class ParClientTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "deveparttools-facade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ format detection

    [TestMethod]
    [DataRow("a.par2", ParFormat.Par2)]
    [DataRow("A.PAR3", ParFormat.Par3)]
    [DataRow("a.par", ParFormat.Par1)]
    [DataRow("a.p01", ParFormat.Par1)]
    [DataRow("a.Q99", ParFormat.Par1)]
    public void FromExtension_KnownExtensions(string name, ParFormat expected) => Assert.AreEqual(expected, ParClient.FromExtension(name));

    [TestMethod]
    [DataRow("a.r00")]
    [DataRow("a.p1x")]
    [DataRow("a.zip")]
    [DataRow("noextension")]
    [DataRow("a.p0")]
    public void FromExtension_OtherExtensions_AreNull(string name) => Assert.IsNull(ParClient.FromExtension(name));

    [TestMethod]
    public void Detect_UsesMagicBytes_NotTheExtension()
    {
        var files = MakeFiles(("one.bin", 3000));
        var par2 = Path.Combine(_dir, "set.par2");
        ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par2, BlockCount = 4 });
        var renamed = Path.Combine(_dir, "set.dat");
        File.Copy(par2, renamed);
        Assert.AreEqual(ParFormat.Par2, ParClient.Detect(renamed));

        var par3 = Path.Combine(_dir, "set.par3");
        ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par3, BlockCount = 4 });
        Assert.AreEqual(ParFormat.Par3, ParClient.Detect(par3));

        var par1 = Path.Combine(_dir, "set.par");
        ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par1 });
        Assert.AreEqual(ParFormat.Par1, ParClient.Detect(par1));
        Assert.AreEqual(ParFormat.Par1, ParClient.Detect(Path.Combine(_dir, "set.p01")), "PAR1 volumes share the index magic");
    }

    [TestMethod]
    public void Detect_MissingFile_FallsBackToExtension()
    {
        Assert.AreEqual(ParFormat.Par2, ParClient.Detect(Path.Combine(_dir, "gone.par2")));
        Assert.ThrowsExactly<InvalidDataException>(() => ParClient.Detect(Path.Combine(_dir, "gone.txt")));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(5)]
    [DataRow(100)]
    public void Detect_NotAParFile_Throws(int length)
    {
        var path = Path.Combine(_dir, "fake.par2");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', length).ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => ParClient.Detect(path));
    }

    // ------------------------------------------------------------------ create / verify / repair, every format

    [TestMethod]
    public void Par2_CreateVerifyRepair_ThroughTheFacade()
    {
        var files = MakeFiles(("a.bin", 50_000), ("sub/b.bin", 20_000), ("c.bin", 1));
        var par2 = Path.Combine(_dir, "set.par2");
        var created = ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par2, BlockCount = 40, RedundancyPercent = 25, Comment = "hello" });
        Assert.AreEqual(ParFormat.Par2, created.Format);
        Assert.IsTrue(created.InputBlocks > 0 && created.RecoveryBlocks > 0 && created.BlockSize > 0);
        Assert.IsTrue(created.WrittenFiles.All(File.Exists));
        StringAssert.Contains(created.Description, "PAR2");

        var clean = ParClient.Verify(par2);
        Assert.AreEqual(ParFormat.Par2, clean.Format);
        Assert.IsTrue(clean.AllFilesComplete);
        Assert.AreEqual(0, clean.MissingBlocks);
        Assert.AreEqual(3, clean.Files.Count);
        Assert.IsTrue(clean.Files.All(f => f.Status == ParFileStatus.Complete));
        CollectionAssert.Contains(clean.Comments.ToList(), "hello");
        Assert.IsTrue(clean.Creators.Count > 0);
        Assert.IsFalse(clean.CriticalDataMissing);
        Assert.IsInstanceOfType<Par2VerifyResult>(clean.Native);

        Corrupt(files[0], 1000, 3000);
        File.Delete(files[2]);
        var damaged = ParClient.Verify(par2);
        Assert.IsFalse(damaged.AllFilesComplete);
        Assert.IsTrue(damaged.RepairPossible);
        Assert.AreEqual(ParFileStatus.Damaged, damaged.Files.Single(f => f.Name == "a.bin").Status);
        Assert.AreEqual(ParFileStatus.Missing, damaged.Files.Single(f => f.Name == "c.bin").Status);

        var repaired = ParClient.Repair(damaged, new ParRepairRequest { PurgeBackups = true });
        Assert.IsTrue(repaired.Success, repaired.Message);
        Assert.IsTrue(repaired.RepairedFiles.Count >= 2);
        Assert.IsTrue(ParClient.Verify(par2).AllFilesComplete);
    }

    [TestMethod]
    public void Par3_CreateVerifyRepair_ProtectsAFolder()
    {
        MakeFiles(("tree/x.bin", 40_000), ("tree/deep/y.bin", 9_000), ("tree/z.txt", 10));
        var par3 = Path.Combine(_dir, "tree.par3");
        var created = ParClient.Create(new ParCreateRequest
        {
            Inputs = [Path.Combine(_dir, "tree")],
            OutputPath = par3,
            BlockCount = 60,
            RedundancyPercent = 30,
            BaseDirectory = _dir,
            Par3Code = Par3Code.Cauchy,
            Par3PackTails = true,
            Par3Reproducible = true,
        });
        Assert.AreEqual(ParFormat.Par3, created.Format);

        var y = Path.Combine(_dir, "tree", "deep", "y.bin");
        var x = Path.Combine(_dir, "tree", "x.bin");
        File.Delete(y);
        Corrupt(x, 100, 200);
        var report = ParClient.Verify(par3, new ParVerifyRequest { BaseDirectory = _dir });
        Assert.AreEqual(ParFormat.Par3, report.Format);
        Assert.IsInstanceOfType<Par3VerifyResult>(report.Native);
        Assert.AreEqual(ParFileStatus.Missing, report.Files.Single(f => f.Path == y).Status);
        Assert.AreEqual(ParFileStatus.Damaged, report.Files.Single(f => f.Path == x).Status);
        Assert.IsTrue(report.RepairPossible);
        Assert.IsTrue(report.BlockSize > 0 && report.BlockCount > 0);

        var repaired = ParClient.Repair(report, new ParRepairRequest { RestoreLinks = true, RestorePermissions = true });
        Assert.IsTrue(repaired.Success, repaired.Message);
        Assert.IsTrue(File.Exists(y));
        Assert.IsTrue(ParClient.Verify(par3, new ParVerifyRequest { BaseDirectory = _dir }).AllFilesComplete);
    }

    [TestMethod]
    public void Par1_CreateVerifyRepair_ExpandsFolders()
    {
        MakeFiles(("p1/one.bin", 4000), ("p1/two.bin", 3000), ("p1/three.bin", 5000));
        // PAR1 stores bare file names, so the set lives next to the files (and is not protected by itself).
        var par1 = Path.Combine(_dir, "p1", "old.par");
        var created = ParClient.Create(new ParCreateRequest { Inputs = [Path.Combine(_dir, "p1")], OutputPath = par1, RecoveryBlockCount = 2, Comment = "legacy" });
        Assert.AreEqual(ParFormat.Par1, created.Format);
        Assert.AreEqual(3, created.InputBlocks);
        Assert.AreEqual(2, created.RecoveryBlocks);

        var report = ParClient.Verify(par1);
        Assert.IsTrue(report.AllFilesComplete);
        CollectionAssert.AreEqual(new[] { "legacy" }, report.Comments.ToList());

        Corrupt(Path.Combine(_dir, "p1", "two.bin"), 10, 50);
        File.Delete(Path.Combine(_dir, "p1", "one.bin"));
        report = ParClient.Verify(par1);
        Assert.IsInstanceOfType<Par1VerifyResult>(report.Native);
        Assert.AreEqual(2, report.MissingBlocks);
        Assert.AreEqual(ParFileStatus.Missing, report.Files.Single(f => f.Name == "one.bin").Status);
        Assert.AreEqual(ParFileStatus.Damaged, report.Files.Single(f => f.Name == "two.bin").Status);
        var repaired = ParClient.Repair(report);
        Assert.IsTrue(repaired.Success, repaired.Message);
        Assert.IsTrue(ParClient.Verify(par1).AllFilesComplete);
    }

    [TestMethod]
    public void Par1_RedundancyDecidesTheVolumeCount_AndTooMuchDamageFailsRepair()
    {
        var files = MakeFiles(("a", 100), ("b", 200), ("c", 300), ("d", 400));
        var par1 = Path.Combine(_dir, "r.par");
        var created = ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par1, RedundancyPercent = 30 });
        Assert.AreEqual(2, created.RecoveryBlocks, "ceil(4 × 30%) = 2");

        foreach (var f in files.Take(3))
        {
            File.Delete(f);
        }

        var report = ParClient.Verify(par1);
        Assert.IsFalse(report.RepairPossible);
        var repaired = ParClient.Repair(report);
        Assert.IsFalse(repaired.Success);
        Assert.AreEqual("Repair failed.", repaired.Message);
    }

    [TestMethod]
    public void Par2_MissingFileDescriptions_GiveUnknownStatus()
    {
        var files = MakeFiles(("u.bin", 10_000));
        var par2 = Path.Combine(_dir, "u.par2");
        var created = ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par2, BlockCount = 10 });

        // Break the body of every file description packet, in every file of the set, so its MD5 check fails.
        foreach (var path in created.WrittenFiles)
        {
            var bytes = File.ReadAllBytes(path);
            var type = "PAR 2.0\0FileDesc"u8.ToArray();
            for (int i = 0; i + 64 + type.Length <= bytes.Length; i++)
            {
                if (bytes.AsSpan(i, 8).SequenceEqual("PAR2\0PKT"u8) && bytes.AsSpan(i + 48, 16).SequenceEqual(type))
                {
                    bytes[i + 64 + 20] ^= 0xFF;
                }
            }

            File.WriteAllBytes(path, bytes);
        }

        var report = ParClient.Verify(par2);
        Assert.AreEqual(ParFileStatus.Unknown, report.Files.Single().Status);
        Assert.IsFalse(report.AllFilesComplete);
    }

    [TestMethod]
    public void Create_FormatOverride_AndVolumeOptions()
    {
        var files = MakeFiles(("v.bin", 64_000));
        var output = Path.Combine(_dir, "custom.recovery");

        var uniform = ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = output, Format = ParFormat.Par2, BlockCount = 64, RecoveryBlockCount = 12, VolumeCount = 3, UniformVolumes = true });
        Assert.AreEqual(ParFormat.Par2, uniform.Format);
        Assert.AreEqual(12, uniform.RecoveryBlocks);
        Assert.AreEqual(4, uniform.WrittenFiles.Count, "index + 3 volumes");

        var limited = ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "lim.par2"), BlockCount = 64, RecoveryBlockCount = 12, LimitVolumeSize = true, FirstRecoveryBlock = 5 });
        Assert.IsTrue(limited.WrittenFiles.Count >= 2);
        Assert.IsTrue(limited.WrittenFiles.Any(f => f.Contains("vol05+", StringComparison.Ordinal)), string.Join(", ", limited.WrittenFiles));

        // No extension, no format: PAR2 is the default.
        var plain = ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "noext"), BlockCount = 8 });
        Assert.AreEqual(ParFormat.Par2, plain.Format);

        var par3 = ParClient.Create(new ParCreateRequest
        {
            Inputs = files,
            OutputPath = Path.Combine(_dir, "opts.par3"),
            BlockSize = 4096,
            RecoveryBlockCount = 5,
            VolumeCount = 2,
            Par3Code = Par3Code.Fft,
            Par3Dedup = Par3Dedup.Blocks,
            Par3StoreData = true,
            Par3UnixPermissions = true,
            Par3FatPermissions = true,
            Par3Interleave = 1,
            Par3MaxRecoveryBlocks = 10,
            Par3CauchyConvention = Par3CauchyConvention.Specification,
        });
        Assert.AreEqual(4096, par3.BlockSize);
        Assert.AreEqual(5, par3.RecoveryBlocks);
        Assert.IsTrue(ParClient.Verify(par3.WrittenFiles[0]).AllFilesComplete);
    }

    [TestMethod]
    public void Verify_FindsRenamedFiles_InExtraFiles()
    {
        var files = MakeFiles(("orig.bin", 30_000));
        var par2 = Path.Combine(_dir, "x.par2");
        ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par2, BlockCount = 30 });
        var moved = Path.Combine(_dir, "renamed.bin");
        File.Move(files[0], moved);
        var report = ParClient.Verify(par2, new ParVerifyRequest { ExtraFiles = [moved] });
        var file = report.Files.Single();
        Assert.AreEqual(moved, file.FoundAs);
        Assert.IsTrue(report.RepairPossible);
    }

    [TestMethod]
    public void Repair_UnknownNativeResult_Throws()
    {
        var report = new ParVerifyReport
        {
            Format = ParFormat.Par2,
            ParFile = "x",
            Files = [],
            BlockSize = 0,
            BlockCount = 0,
            MissingBlocks = 0,
            RecoveryBlocksAvailable = 0,
            AllFilesComplete = true,
            RepairPossible = true,
            Native = "not a verification result",
        };
        Assert.ThrowsExactly<ArgumentException>(() => ParClient.Repair(report));
    }

    [TestMethod]
    public void ExpandFiles_RecursesSortsAndSkipsParFiles()
    {
        MakeFiles(("e/b.bin", 1), ("e/a.bin", 1), ("e/sub/c.bin", 1), ("e/set.par2", 1), ("e/set.vol0+1.par2", 1), ("e/old.p01", 1), ("e/new.par3", 1), ("explicit.par2", 1));
        var dir = Path.Combine(_dir, "e");
        var explicitPar = Path.Combine(_dir, "explicit.par2");

        var all = ParClient.ExpandFiles([dir, explicitPar], recursive: true);
        CollectionAssert.AreEqual(
            new[] { Path.Combine(dir, "a.bin"), Path.Combine(dir, "b.bin"), Path.Combine(dir, "sub", "c.bin"), explicitPar },
            all);

        var top = ParClient.ExpandFiles([dir], recursive: false);
        CollectionAssert.AreEqual(new[] { Path.Combine(dir, "a.bin"), Path.Combine(dir, "b.bin") }, top);
    }

    [TestMethod]
    public void Operations_ReportProgressThroughTheContext()
    {
        var files = MakeFiles(("p.bin", 100_000));
        var observer = new RecordingObserver();
        var context = new OperationContext { Observer = observer };
        BlockStateMap? map = null;
        context.BlocksCreated += m => map = m;
        ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = Path.Combine(_dir, "p.par2"), BlockCount = 50 }, context);
        Assert.IsTrue(observer.Stages.Count > 0);
        Assert.IsNotNull(map);
    }

    private sealed class RecordingObserver : IParOperationObserver
    {
        public List<string> Stages { get; } = [];

        public void Stage(string name) => Stages.Add(name);

        public void Progress(double fraction, string? detail)
        {
        }

        public void Log(ParLogLevel level, string message)
        {
        }
    }

    private List<string> MakeFiles(params (string Name, int Size)[] specs)
    {
        var rnd = new Random(42);
        var list = new List<string>();
        foreach (var (name, size) in specs)
        {
            var path = Path.Combine(_dir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(path, data);
            list.Add(path);
        }

        return list;
    }

    private static void Corrupt(string path, int offset, int count)
    {
        var data = File.ReadAllBytes(path);
        for (int i = offset; i < offset + count && i < data.Length; i++)
        {
            data[i] ^= 0x5A;
        }

        File.WriteAllBytes(path, data);
    }

    [TestMethod]
    public void Extend_Par3_AndUnsupportedFormats()
    {
        var files = MakeFiles(("x/one.bin", 70_000));
        var par3 = Path.Combine(_dir, "x", "one.par3");
        ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par3, RecoveryBlockCount = 2, BlockSize = 4096, Par3Code = Par3Code.Cauchy });
        var extended = ParClient.Extend(new ParExtendRequest { ParFile = par3, RecoveryBlockCount = 3, FirstRecoveryBlock = 10, VolumeCount = 1 });
        Assert.AreEqual(ParFormat.Par3, extended.Format);
        Assert.AreEqual(3, extended.RecoveryBlocks);
        Assert.IsTrue(extended.WrittenFiles.Single().EndsWith("one.vol10+03.par3", StringComparison.Ordinal), extended.WrittenFiles.Single());
        Assert.AreEqual(5, ParClient.Verify(par3).RecoveryBlocksAvailable);

        var par1 = Path.Combine(_dir, "x", "one.par");
        ParClient.Create(new ParCreateRequest { Inputs = files, OutputPath = par1 });
        Assert.ThrowsExactly<NotSupportedException>(() => ParClient.Extend(new ParExtendRequest { ParFile = par1, RecoveryBlockCount = 1 }));
    }
}

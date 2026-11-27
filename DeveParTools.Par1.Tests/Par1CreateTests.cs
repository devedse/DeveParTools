using DeveParTools.Core.Operations;
using DeveParTools.Par1.Tests.TestSupport;

namespace DeveParTools.Par1.Tests;

[TestClass]
public sealed class Par1CreateTests
{
    [TestMethod]
    public void Create_WritesIndexAndVolumes_SortedByName()
    {
        using var dir = new TempDir();
        var c = dir.WriteRandom("c.bin", 300, 1);
        var a = dir.WriteRandom("a.bin", 1000, 2);
        var b = dir.WriteRandom("b.bin", 0, 3);
        var ctx = new OperationContext { Threads = 1 };

        // Relative and duplicate spellings of the same input are collapsed; the output path may lack ".par".
        var written = Par1Set.Create(new Par1CreateOptions { InputFiles = [c, a, b, a, Path.Combine(dir.Path, ".", "c.bin")], OutputPath = dir["set"], RecoveryVolumes = 2 }, ctx);
        CollectionAssert.AreEqual(new[] { dir["set.par"], dir["set.p01"], dir["set.p02"] }, written.ToArray());
        Assert.IsFalse(Directory.EnumerateFiles(dir.Path, "*.tmp").Any(), "temporary volume data is removed");

        var index = Par1File.Read(dir["set.par"])!;
        CollectionAssert.AreEqual(new[] { "a.bin", "b.bin", "c.bin" }, index.Files.Select(f => f.Name).ToArray());
        Assert.IsTrue(index.Files.All(f => f.IsSaved));
        Assert.AreEqual(string.Empty, index.Comment ?? string.Empty);

        // Every volume carries the full file list and is as large as the largest file.
        for (int v = 1; v <= 2; v++)
        {
            var vol = Par1File.Read(dir[$"set.p0{v}"])!;
            Assert.AreEqual((ulong)v, vol.VolumeNumber);
            Assert.AreEqual(1000, vol.DataSize);
            Assert.IsTrue(vol.ControlHashValid);
            CollectionAssert.AreEqual(index.SetHash, vol.SetHash);
        }

        // Volume 1 is the XOR of all files (zero-padded to the largest one).
        var expected = new byte[1000];
        foreach (var f in new[] { a, b, c })
        {
            var d = File.ReadAllBytes(f);
            for (int i = 0; i < d.Length; i++)
            {
                expected[i] ^= d[i];
            }
        }

        var v1 = Par1File.Read(dir["set.p01"])!;
        CollectionAssert.AreEqual(expected, File.ReadAllBytes(dir["set.p01"]).AsSpan((int)v1.DataOffset).ToArray());
    }

    [TestMethod]
    public void Create_NoVolumes_WritesOnlyTheIndexWithItsComment()
    {
        using var dir = new TempDir();
        var a = dir.WriteRandom("a.bin", 10, 1);
        var written = Par1Set.Create(new Par1CreateOptions { InputFiles = [a], OutputPath = dir["only.PAR"], RecoveryVolumes = 0, Comment = "just the index" });
        CollectionAssert.AreEqual(new[] { dir["only.par"] }, written.ToArray(), "the extension is normalised to lower case");
        Assert.AreEqual("just the index", Par1File.Read(dir["only.par"])!.Comment);
        Assert.IsTrue(Par1Set.Verify(dir["only.par"]).AllFilesComplete);
    }

    [TestMethod]
    public void Create_OnlyEmptyFiles_GivesEmptyVolumes()
    {
        using var dir = new TempDir();
        var e = dir.Write("empty.bin", []);
        Par1Set.Create(new Par1CreateOptions { InputFiles = [e], OutputPath = dir["e.par"] });
        Assert.AreEqual(0, Par1File.Read(dir["e.p01"])!.DataSize);

        File.Delete(e);
        var verify = Par1Set.Verify(dir["e.par"]);
        Assert.IsTrue(Par1Set.Repair(verify));
        Assert.AreEqual(0, new FileInfo(e).Length);
    }

    [TestMethod]
    public void Create_RejectsBadCounts()
    {
        using var dir = new TempDir();
        var a = dir.WriteRandom("a.bin", 10, 1);
        Assert.ThrowsExactly<ArgumentException>(() => Par1Set.Create(new Par1CreateOptions { InputFiles = [], OutputPath = dir["x.par"] }));
        Assert.ThrowsExactly<ArgumentException>(() => Par1Set.Create(new Par1CreateOptions { InputFiles = [a], OutputPath = dir["x.par"], RecoveryVolumes = -1 }));
        Assert.ThrowsExactly<ArgumentException>(() => Par1Set.Create(new Par1CreateOptions { InputFiles = [a], OutputPath = dir["x.par"], RecoveryVolumes = 255 }));
        Assert.IsFalse(File.Exists(dir["x.par"]));
    }

    [TestMethod]
    public void Create_RejectsInputsOutsideTheParDirectory()
    {
        using var dir = new TempDir();
        var a = dir.WriteRandom("a.bin", 10, 1);
        var sub = dir.WriteRandom("sub/b.bin", 10, 2);

        // PAR1 stores bare names and Verify looks next to the .par file, so sub/b.bin could never be found again.
        var e = Assert.ThrowsExactly<ArgumentException>(() => Par1Set.Create(new Par1CreateOptions { InputFiles = [a, sub], OutputPath = dir["set.par"] }));
        StringAssert.Contains(e.Message, sub);
        StringAssert.Contains(e.Message, "bare file names");

        // The same holds when the .par file is elsewhere (here in the subdirectory).
        Assert.ThrowsExactly<ArgumentException>(() => Par1Set.Create(new Par1CreateOptions { InputFiles = [a], OutputPath = dir["sub/set.par"] }));
        Assert.IsFalse(File.Exists(dir["set.par"]) || File.Exists(dir["sub/set.par"]), "nothing is written");

        // A relative output path is resolved against the current directory before the check.
        Assert.ThrowsExactly<ArgumentException>(() => Par1Set.Create(new Par1CreateOptions { InputFiles = [a], OutputPath = "relative-set.par" }));
    }

    [TestMethod]
    public void Create_RejectsTwoInputsWithTheSameName()
    {
        using var dir = new TempDir();
        var a = dir.WriteRandom("a.bin", 10, 1);
        var other = dir.WriteRandom("other/a.bin", 20, 2);
        var e = Assert.ThrowsExactly<ArgumentException>(() => Par1Set.Create(new Par1CreateOptions { InputFiles = [a, other], OutputPath = dir["set.par"] }));
        StringAssert.Contains(e.Message, "'a.bin' occurs more than once");
        StringAssert.Contains(e.Message, other);
        Assert.IsFalse(File.Exists(dir["set.par"]));
    }

    [TestMethod]
    public void Create_ReportsStagesAndProgress()
    {
        using var dir = new TempDir();
        var a = dir.WriteRandom("a.bin", 5000, 1);
        var b = dir.WriteRandom("b.bin", 7000, 2);
        var observer = new RecordingObserver();
        Par1Set.Create(new Par1CreateOptions { InputFiles = [a, b], OutputPath = dir["set.par"], RecoveryVolumes = 1 }, new OperationContext { Observer = observer });
        CollectionAssert.AreEqual(new[] { "Hashing", "Writing the index file", "Encoding", "Writing recovery files" }, observer.Stages.ToArray());
        CollectionAssert.AreEqual(observer.Stages.ToArray(), observer.Plan.Select(p => p.Name).ToArray(), "every step was announced, in order");
        Assert.IsTrue(observer.Plan.All(p => p.Weight > 0 && p.Description.Length > 20));

        var verifying = new RecordingObserver();
        var verify = Par1Set.Verify(dir["set.par"], context: new OperationContext { Observer = verifying });
        CollectionAssert.AreEqual(new[] { "Loading", "Verifying" }, verifying.Stages.ToArray());
        File.Delete(a);
        var repairing = new RecordingObserver();
        Assert.IsTrue(Par1Set.Repair(Par1Set.Verify(dir["set.par"]), new OperationContext { Observer = repairing }));
        CollectionAssert.AreEqual(new[] { "Copying good blocks", "Reconstructing", "Writing repaired files" }, repairing.Stages.ToArray());
        Assert.IsTrue(verify.AllFilesComplete);
    }

    private sealed class RecordingObserver : IParOperationObserver
    {
        public List<string> Stages { get; } = new();

        public IReadOnlyList<OperationStage> Plan { get; private set; } = [];

        void IParOperationObserver.Plan(IReadOnlyList<OperationStage> stages) => Plan = stages;

        public void Stage(string name)
        {
            lock (Stages)
            {
                Stages.Add(name);
            }
        }

        public void Progress(double fraction, string? detail = null)
        {
        }

        public void Log(ParLogLevel level, string message)
        {
        }
    }
}

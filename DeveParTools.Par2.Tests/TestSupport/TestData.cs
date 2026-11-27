using DeveParTools.Core.Operations;

namespace DeveParTools.Par2.Tests.TestSupport;

internal static class TestData
{
    public static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Writes the files (name → size) with deterministic contents and returns path → contents.</summary>
    public static Dictionary<string, byte[]> WriteFiles(TempDir dir, int seed, params (string Name, int Size)[] files)
    {
        var result = new Dictionary<string, byte[]>();
        int i = 0;
        foreach (var (name, size) in files)
        {
            var data = Random(size, seed * 1000 + i++);
            result[dir.Write(name, data)] = data;
        }

        return result;
    }

    public static void AssertFiles(Dictionary<string, byte[]> files)
    {
        foreach (var (path, data) in files)
        {
            Assert.IsTrue(File.Exists(path), path);
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    /// <summary>Flips the bytes [offset, offset + count) of a file.</summary>
    public static void Corrupt(string path, long offset, int count = 1)
    {
        var b = File.ReadAllBytes(path);
        for (int i = 0; i < count; i++)
        {
            b[offset + i] ^= 0xA5;
        }

        File.WriteAllBytes(path, b);
    }

    public static Par2CreateResult Create(TempDir dir, IEnumerable<string> files, long sliceSize, int recovery, string name = "set.par2", OperationContext? context = null)
        => Par2Creator.Create(new Par2CreateOptions { InputFiles = files.ToList(), OutputPath = dir[name], SliceSize = sliceSize, RecoverySliceCount = recovery }, context);

    public static Par2VerifyResult Verify(TempDir dir, string name = "set.par2", OperationContext? context = null)
        => Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = dir[name] }, context);
}

/// <summary>Records everything an operation reports (thread-safe).</summary>
internal sealed class RecordingObserver : IParOperationObserver
{
    private readonly Lock _lock = new();

    public List<string> Stages { get; } = new();

    /// <summary>Every plan announced, in order (operations refine their plan as they learn more).</summary>
    public List<IReadOnlyList<OperationStage>> Plans { get; } = new();

    public List<(ParLogLevel Level, string Message)> Logs { get; } = new();

    public void Plan(IReadOnlyList<OperationStage> stages)
    {
        lock (_lock)
        {
            Plans.Add(stages);
        }
    }

    /// <summary>Every stage that ran was in the last plan, in the plan's order.</summary>
    public void AssertStagesFollowThePlan()
    {
        var plan = Plans[^1].Select(p => p.Name).ToList();
        int at = 0;
        foreach (var stage in Stages)
        {
            int found = plan.IndexOf(stage, at);
            Assert.IsTrue(found >= 0, $"stage {stage} is not in the plan ({string.Join(", ", plan)}) after position {at}");
            at = found;
        }
    }

    public List<double> Fractions { get; } = new();

    public void Stage(string name)
    {
        lock (_lock)
        {
            Stages.Add(name);
        }
    }

    public void Progress(double fraction, string? detail = null)
    {
        lock (_lock)
        {
            Fractions.Add(fraction);
        }
    }

    public void Log(ParLogLevel level, string message)
    {
        lock (_lock)
        {
            Logs.Add((level, message));
        }
    }
}

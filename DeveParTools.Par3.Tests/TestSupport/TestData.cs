using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3.Tests.TestSupport;

internal static class TestData
{
    /// <summary>Deterministic pseudo-random bytes.</summary>
    public static byte[] Random(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Flips the bits of <paramref name="count"/> bytes starting at <paramref name="offset"/>.</summary>
    public static void Corrupt(string path, long offset, int count = 1)
    {
        var data = File.ReadAllBytes(path);
        for (int i = 0; i < count; i++)
        {
            data[offset + i] ^= 0xA5;
        }

        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Changes the bytes of the packet at <paramref name="packetOffset"/> and recomputes its checksum, so the result
    /// is a valid packet with other contents (what a buggy or malicious creator would write).
    /// </summary>
    public static void RewritePacket(string path, long packetOffset, Action<byte[]> modify)
    {
        var data = File.ReadAllBytes(path);
        long length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)packetOffset + 24));
        var packet = data.AsSpan((int)packetOffset, (int)length).ToArray();
        modify(packet);
        Core.Hashing.Blake3.Hash(packet.AsSpan(24)).AsSpan(0, 16).CopyTo(packet.AsSpan(8));
        packet.CopyTo(data, packetOffset);
        File.WriteAllBytes(path, data);
    }

    /// <summary>Inserts bytes at <paramref name="offset"/>, shifting the rest of the file.</summary>
    public static void Insert(string path, int offset, byte[] bytes)
    {
        var data = File.ReadAllBytes(path).ToList();
        data.InsertRange(offset, bytes);
        File.WriteAllBytes(path, data.ToArray());
    }
}

/// <summary>Observer that records everything (thread-safe).</summary>
internal sealed class RecordingObserver : IParOperationObserver
{
    private readonly object _lock = new();

    public List<string> Stages { get; } = new();

    /// <summary>Every plan announced, in order.</summary>
    public List<IReadOnlyList<OperationStage>> Plans { get; } = new();

    /// <summary>Every progress fraction, per stage.</summary>
    public Dictionary<string, List<double>> Fractions { get; } = new();

    public List<(ParLogLevel Level, string Message)> Logs { get; } = new();

    public double LastProgress { get; private set; }

    public void Plan(IReadOnlyList<OperationStage> stages)
    {
        lock (_lock)
        {
            Plans.Add(stages);
        }
    }

    /// <summary>Every stage that ran was announced in the last plan, in its order, and progress stayed in [0, 1].</summary>
    public void AssertStagesFollowThePlan(params string[] unplanned)
    {
        var plan = Plans[^1].Select(p => p.Name).ToList();
        int at = 0;
        foreach (var stage in Stages.Where(s => !unplanned.Contains(s)))
        {
            int found = plan.IndexOf(stage, at);
            Assert.IsTrue(found >= 0, $"stage {stage} is not in the plan ({string.Join(", ", plan)}) after position {at}; ran: {string.Join(", ", Stages)}");
            at = found;
        }

        Assert.IsTrue(Fractions.Values.SelectMany(f => f).All(f => f is >= 0 and <= 1), "progress within [0, 1]");
        Assert.IsTrue(Plans[^1].All(p => p.Weight >= 0 && p.Description.Length > 20), "every step has a weight and a description");
    }

    public IEnumerable<string> Warnings => Logs.Where(l => l.Level >= ParLogLevel.Warning).Select(l => l.Message);

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
            LastProgress = fraction;
            var stage = Stages.Count == 0 ? string.Empty : Stages[^1];
            if (!Fractions.TryGetValue(stage, out var list))
            {
                Fractions[stage] = list = new List<double>();
            }

            list.Add(fraction);
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

internal static class Par3Files
{
    /// <summary>Reads and assembles the set of a .par3 file and its volumes.</summary>
    public static Par3InputSet Load(string par3Path)
        => Par3InputSet.Assemble(Par3InputSet.DiscoverFiles(par3Path).Select(f => Par3PacketReader.ReadFile(f)).ToList())!;

    /// <summary>All packets of one file.</summary>
    public static List<Par3Packet> Packets(string path) => Par3PacketReader.ReadFile(path).Packets;

    public static Par3VerifyResult Verify(string par3Path, OperationContext? context = null, IReadOnlyList<string>? extraData = null, string? baseDirectory = null)
        => Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = par3Path, ExtraDataFiles = extraData ?? [], BaseDirectory = baseDirectory }, context);
}

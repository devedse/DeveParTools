namespace DeveParTools.Core.Operations;

public enum ParLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Receives progress information from long-running operations. Implementations must be thread-safe.</summary>
public interface IParOperationObserver
{
    /// <summary>A new stage starts (e.g. "Hashing", "Encoding", "Scanning", "Repairing").</summary>
    void Stage(string name);

    /// <summary>Progress of the current stage in [0, 1].</summary>
    void Progress(double fraction, string? detail = null);

    void Log(ParLogLevel level, string message);
}

public sealed class NullObserver : IParOperationObserver
{
    public static readonly NullObserver Instance = new();

    public void Stage(string name)
    {
    }

    public void Progress(double fraction, string? detail = null)
    {
    }

    public void Log(ParLogLevel level, string message)
    {
    }
}

/// <summary>Settings and sinks shared by every operation.</summary>
public sealed class OperationContext
{
    public IParOperationObserver Observer { get; init; } = NullObserver.Instance;

    /// <summary>Live per-block state, created by the operation once the block count is known.</summary>
    public BlockStateMap? Blocks { get; set; }

    /// <summary>Raised when <see cref="Blocks"/> is (re)created.</summary>
    public event Action<BlockStateMap>? BlocksCreated;

    public CancellationToken CancellationToken { get; init; }

    public int Threads { get; init; } = Environment.ProcessorCount;

    /// <summary>Memory budget for coding buffers in bytes.</summary>
    public long MemoryBudget { get; init; } = DefaultMemoryBudget();

    public static OperationContext Default => new();

    public BlockStateMap CreateBlockMap(int inputBlocks, int recoveryBlocks)
    {
        var map = new BlockStateMap(inputBlocks, recoveryBlocks);
        Blocks = map;
        BlocksCreated?.Invoke(map);
        return map;
    }

    private static long DefaultMemoryBudget()
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0)
        {
            return 512L << 20;
        }

        return Math.Clamp(total / 4, 64L << 20, 8L << 30);
    }

    public LinearCombineOptionsFactory Combine => new(this);

    public readonly struct LinearCombineOptionsFactory(OperationContext ctx)
    {
        public Codes.LinearCombineOptions Create(IProgress<double>? progress = null, long? budget = null) => new()
        {
            MemoryBudget = budget ?? ctx.MemoryBudget,
            Threads = ctx.Threads,
            CancellationToken = ctx.CancellationToken,
            Progress = progress,
        };
    }
}

namespace DeveParTools.Core.Operations;

public enum ParLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// One step of an operation, announced before it starts (<see cref="IParOperationObserver.Plan"/>) so a UI can show
/// every step, how much of the work it is, and what it does.
/// </summary>
/// <param name="Name">The name <see cref="IParOperationObserver.Stage"/> is called with when the step starts.</param>
/// <param name="Weight">Estimated amount of work, relative to the other steps (for example bytes read or written).</param>
/// <param name="Description">What the step does, in plain words.</param>
public sealed record OperationStage(string Name, double Weight, string Description);

/// <summary>Receives progress information from long-running operations. Implementations must be thread-safe.</summary>
/// <remarks>
/// An operation first announces its steps with <see cref="Plan"/> (again when it learns more, e.g. once the file sizes
/// are known), then for every step calls <see cref="Stage"/> and reports that step's progress from 0 to 1 with
/// <see cref="Progress"/>. Example for creating a PAR3 set:
/// <code>
/// Plan([Preparing 0.1%, Hashing 47%, Writing metadata 0.5%, Encoding 49%, Writing recovery files 3%, Writing the index file 0.4%])
/// Stage("Preparing")  Progress(1)
/// Stage("Hashing")    Progress(0.01) … Progress(1)
/// …
/// </code>
/// </remarks>
public interface IParOperationObserver
{
    /// <summary>The steps the operation will take, in order, with their estimated share of the work.</summary>
    void Plan(IReadOnlyList<OperationStage> stages)
    {
    }

    /// <summary>A new stage starts (e.g. "Hashing", "Encoding", "Scanning", "Repairing"); its progress starts at 0.</summary>
    void Stage(string name);

    /// <summary>Progress of the current stage in [0, 1].</summary>
    void Progress(double fraction, string? detail = null);

    void Log(ParLogLevel level, string message);
}

/// <summary>Helpers for reporting progress.</summary>
public static class ObserverExtensions
{
    /// <summary>
    /// An <see cref="IProgress{T}"/> that passes fractions straight to the observer, on the reporting thread.
    /// (<see cref="Progress{T}"/> would queue every report to the thread pool, where they can arrive out of order.)
    /// </summary>
    public static IProgress<double> ToProgress(this IParOperationObserver observer, string? detail = null) => new ObserverProgress(observer, detail, 0, 1);

    /// <summary>Like <see cref="ToProgress(IParOperationObserver, string?)"/>, for a part of a stage: 0..1 becomes <paramref name="from"/>..<paramref name="to"/>.</summary>
    public static IProgress<double> ToProgress(this IParOperationObserver observer, double from, double to) => new ObserverProgress(observer, null, from, to);

    private sealed class ObserverProgress(IParOperationObserver observer, string? detail, double from, double to) : IProgress<double>
    {
        public void Report(double value) => observer.Progress(from + (to - from) * value, detail);
    }
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

    /// <summary>
    /// A context for an operation that runs inside another one (extending a set verifies it first): the same settings
    /// and the same observer, except that the inner operation's <see cref="IParOperationObserver.Plan"/> is ignored (the
    /// outer operation announced all steps), and block maps it creates are passed on to this context.
    /// </summary>
    public OperationContext Nested()
    {
        var child = new OperationContext
        {
            Observer = new PlanlessObserver(Observer),
            CancellationToken = CancellationToken,
            Threads = Threads,
            MemoryBudget = MemoryBudget,
            Blocks = Blocks,
        };
        child.BlocksCreated += map =>
        {
            Blocks = map;
            BlocksCreated?.Invoke(map);
        };
        return child;
    }

    private sealed class PlanlessObserver(IParOperationObserver inner) : IParOperationObserver
    {
        public void Plan(IReadOnlyList<OperationStage> stages)
        {
        }

        public void Stage(string name) => inner.Stage(name);

        public void Progress(double fraction, string? detail = null) => inner.Progress(fraction, detail);

        public void Log(ParLogLevel level, string message) => inner.Log(level, message);
    }

    public BlockStateMap CreateBlockMap(int inputBlocks, int recoveryBlocks)
    {
        var map = new BlockStateMap(inputBlocks, recoveryBlocks);
        Blocks = map;
        BlocksCreated?.Invoke(map);
        return map;
    }

    private static long DefaultMemoryBudget() => MemoryBudgetFor(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// A quarter of the memory available to the process, between 64 MiB and 8 GiB; 512 MiB when the runtime does not
    /// know (reports 0). E.g. 16 GiB available → 4 GiB budget, 128 MiB available → 64 MiB.
    /// </summary>
    internal static long MemoryBudgetFor(long totalAvailableMemory)
        => totalAvailableMemory <= 0 ? 512L << 20 : Math.Clamp(totalAvailableMemory / 4, 64L << 20, 8L << 30);

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

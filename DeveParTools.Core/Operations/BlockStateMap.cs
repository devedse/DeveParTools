using System.Collections.Concurrent;

namespace DeveParTools.Core.Operations;

/// <summary>State of one input (data) block, as shown in the block grid.</summary>
public enum BlockState : byte
{
    /// <summary>Not checked yet.</summary>
    Unknown = 0,

    /// <summary>Being checked right now.</summary>
    Scanning,

    /// <summary>Present at its expected position.</summary>
    Found,

    /// <summary>Present in the same file but at another offset (data shifted).</summary>
    FoundMoved,

    /// <summary>Present in another file (renamed file, extra file, other file of the set).</summary>
    FoundElsewhere,

    /// <summary>Present at its location but with wrong contents.</summary>
    Damaged,

    /// <summary>Not found anywhere.</summary>
    Missing,

    /// <summary>Missing, but enough recovery data exists to rebuild it.</summary>
    Recoverable,

    /// <summary>Being rebuilt right now.</summary>
    Repairing,

    /// <summary>Rebuilt and verified.</summary>
    Repaired,

    /// <summary>Missing and not enough recovery data.</summary>
    Unrecoverable,

    /// <summary>Block is computed or written (create).</summary>
    Processed,

    /// <summary>Not found in the files, but stored in the PAR files themselves (PAR3 "store data"): repair copies it back.</summary>
    FoundStored,
}

/// <summary>State of one recovery block.</summary>
public enum RecoveryBlockState : byte
{
    Unknown = 0,
    Available,
    Damaged,
    Missing,
    Used,
    Created,
}

[Flags]
public enum BlockFlags : byte
{
    None = 0,

    /// <summary>The block is referenced by more than one file (PAR3 dedup).</summary>
    Deduplicated = 1,

    /// <summary>The block contains the tails of several files (PAR3 tail packing).</summary>
    TailPacked = 2,

    /// <summary>The block contains zero padding at the end of a file.</summary>
    Padded = 4,
}

/// <summary>Where a block was found (or where it belongs).</summary>
public readonly record struct BlockLocation(string File, long Offset, int Length);

/// <summary>
/// Lock-free shared per-block state for live visualisation. Writers (engine threads) use <see cref="Set"/>;
/// readers (UI) poll <see cref="Version"/> and read the arrays, optionally only the dirty ranges.
/// </summary>
public sealed class BlockStateMap
{
    private readonly byte[] _states;
    private readonly byte[] _recovery;
    private readonly byte[] _flags;
    private readonly long[] _dirty; // one bit per group of 64 blocks
    private long _version;

    public BlockStateMap(int inputBlocks, int recoveryBlocks)
    {
        _states = new byte[inputBlocks];
        _flags = new byte[inputBlocks];
        _recovery = new byte[recoveryBlocks];
        _dirty = new long[(inputBlocks / 64 + 64) / 64 + 1];
    }

    public int InputBlockCount => _states.Length;

    public int RecoveryBlockCount => _recovery.Length;

    /// <summary>Incremented on every change.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Block → file(s) and byte ranges it belongs to (filled by the format layer).</summary>
    public ConcurrentDictionary<int, BlockLocation[]> Ownership { get; } = new();

    /// <summary>Block → where it was actually found (when not in place).</summary>
    public ConcurrentDictionary<int, BlockLocation> FoundAt { get; } = new();

    /// <summary>Optional names of the recovery block containers (volume files), per recovery block.</summary>
    public ConcurrentDictionary<int, string> RecoveryContainer { get; } = new();

    public BlockState Get(int index) => (BlockState)Volatile.Read(ref _states[index]);

    public BlockFlags GetFlags(int index) => (BlockFlags)Volatile.Read(ref _flags[index]);

    public RecoveryBlockState GetRecovery(int index) => (RecoveryBlockState)Volatile.Read(ref _recovery[index]);

    public void Set(int index, BlockState state)
    {
        Volatile.Write(ref _states[index], (byte)state);
        MarkDirty(index);
    }

    public void SetRange(int start, int count, BlockState state)
    {
        for (int i = start; i < start + count; i++)
        {
            Volatile.Write(ref _states[i], (byte)state);
        }

        for (int i = start; i < start + count; i += 64)
        {
            MarkDirty(i);
        }

        if (count > 0)
        {
            MarkDirty(start + count - 1);
        }
    }

    public void AddFlags(int index, BlockFlags flags)
    {
        Volatile.Write(ref _flags[index], (byte)(_flags[index] | (byte)flags));
        MarkDirty(index);
    }

    public void SetRecovery(int index, RecoveryBlockState state)
    {
        Volatile.Write(ref _recovery[index], (byte)state);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Copy current states into <paramref name="destination"/> (length ≥ InputBlockCount).</summary>
    public void CopyStates(Span<byte> destination) => _states.AsSpan().CopyTo(destination);

    public void CopyRecoveryStates(Span<byte> destination) => _recovery.AsSpan().CopyTo(destination);

    /// <summary>Count blocks per state.</summary>
    public int[] CountStates()
    {
        var counts = new int[Enum.GetValues<BlockState>().Length];
        foreach (var s in _states)
        {
            counts[s]++;
        }

        return counts;
    }

    public int[] CountRecoveryStates()
    {
        var counts = new int[Enum.GetValues<RecoveryBlockState>().Length];
        foreach (var s in _recovery)
        {
            counts[s]++;
        }

        return counts;
    }

    /// <summary>Returns and clears the dirty flag of the 64-block group containing <paramref name="group"/>·64.</summary>
    public bool TakeDirtyGroup(int group)
    {
        int word = group >> 6;
        long bit = 1L << (group & 63);
        if (word >= _dirty.Length)
        {
            return false;
        }

        long old = Interlocked.And(ref _dirty[word], ~bit);
        return (old & bit) != 0;
    }

    private void MarkDirty(int index)
    {
        int group = index >> 6;
        Interlocked.Or(ref _dirty[group >> 6], 1L << (group & 63));
        Interlocked.Increment(ref _version);
    }
}

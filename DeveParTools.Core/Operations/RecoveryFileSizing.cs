namespace DeveParTools.Core.Operations;

/// <summary>How recovery blocks are spread over recovery files (PAR2 .volXX+YY.par2, PAR3 .volXX+YY.par3).</summary>
public enum RecoveryFileScheme
{
    /// <summary>
    /// 1, 2, 4, 8, ... blocks per file (par2cmdline's default). With a file count n: exactly n files, low, 2·low,
    /// 4·low, ... with the smallest power of two "low" for which they hold everything (par2cmdline -n).
    /// </summary>
    Exponential,

    /// <summary>The same number of blocks in every file (default 1 file; par2cmdline -u).</summary>
    Uniform,

    /// <summary>
    /// Like <see cref="Exponential"/>, but no file holds more blocks than the largest input file has (par2cmdline -l);
    /// a file count is not used.
    /// </summary>
    LimitSize,
}

/// <summary>A recovery file to write: its path, the number of its first recovery block, and how many it holds.</summary>
public sealed record RecoveryFilePlan(string Path, long FirstBlock, int Count);

/// <summary>
/// A file that creating a set will write, with its exact size: recovery blocks plus everything around them (packet
/// headers, checksums, the file list and its copies). Computed by the formats' PlanFileSizes without reading the input
/// files.
/// </summary>
/// <param name="RecoveryBlocks">Recovery blocks in the file (0 for the index file and PAR3 data parts).</param>
public sealed record OutputFileSize(string Path, long Bytes, int RecoveryBlocks);

/// <summary>How many recovery blocks go into each recovery file. Shared by PAR2 and PAR3 so both follow one set of rules.</summary>
/// <remarks>
/// Blocks per file for 10 recovery blocks:
/// <code>
/// Exponential               1, 2, 4, 3      (the last file holds what is left)
/// Exponential, 2 files      4, 6            low = 4: the smallest power of two with low·(2² − 1) ≥ 10
/// Uniform, 3 files          4, 3, 3         (the first files get one more when it does not divide evenly)
/// Uniform, no count         10              (one file)
/// LimitSize, limit 3        1, 2, 3, 3, 1   (doubling, but never more than 3)
/// </code>
/// There is at most one file per block and never an empty file: "Uniform, 5 files" for 2 blocks gives 1, 1.
/// </remarks>
public static class RecoveryFileSizing
{
    /// <param name="recoveryCount">Total number of recovery blocks.</param>
    /// <param name="scheme">The distribution.</param>
    /// <param name="fileCount">Requested number of files (Exponential and Uniform; ignored by LimitSize). Null or 0: automatic.</param>
    /// <param name="limitBlocks">For LimitSize: the most blocks one file may hold (the largest input file's block count).</param>
    public static List<int> Counts(int recoveryCount, RecoveryFileScheme scheme, int? fileCount, long limitBlocks)
    {
        if (recoveryCount <= 0)
        {
            return [];
        }

        int? count = fileCount is > 0 ? fileCount : null;
        return scheme switch
        {
            RecoveryFileScheme.Uniform => UniformCounts(recoveryCount, count ?? 1),
            RecoveryFileScheme.LimitSize => ExponentialCounts(recoveryCount, null, (int)Math.Clamp(limitBlocks, 1, int.MaxValue)),
            _ => ExponentialCounts(recoveryCount, count, int.MaxValue),
        };
    }

    /// <summary>n files (at most one per block), the first ones one block larger: 10 in 3 → 4, 3, 3.</summary>
    private static List<int> UniformCounts(int recoveryCount, int fileCount)
    {
        int n = Math.Clamp(fileCount, 1, recoveryCount);
        var counts = new List<int>(n);
        int remaining = recoveryCount;
        for (int i = 0; i < n; i++)
        {
            int c = (remaining + (n - i) - 1) / (n - i);
            counts.Add(c);
            remaining -= c;
        }

        return counts;
    }

    /// <summary>
    /// low, 2·low, 4·low, ... (each at most <paramref name="limit"/>) until all blocks are placed. low = 1, or for a
    /// given file count n the smallest power of two with low·(2^n − 1) ≥ the block count (as par2cmdline -n).
    /// </summary>
    private static List<int> ExponentialCounts(int recoveryCount, int? fileCount, int limit)
    {
        long next = 1;
        if (fileCount is int n)
        {
            long capacity = (1L << Math.Clamp(n, 1, 31)) - 1;
            while (next * capacity < recoveryCount)
            {
                next *= 2;
            }
        }

        var counts = new List<int>();
        for (int remaining = recoveryCount; remaining > 0;)
        {
            int c = (int)Math.Min(Math.Min(next, remaining), limit);
            counts.Add(c);
            remaining -= c;
            next = Math.Min(int.MaxValue, next * 2);
        }

        return counts;
    }
}

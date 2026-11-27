namespace DeveParTools.Core.Operations;

/// <summary>
/// The largest block (PAR2: slice) DeveParTools creates: 64 MiB, or for very large sets the size that 32,768 blocks
/// need, so a set never has to have more blocks than that because of the limit.
/// </summary>
/// <remarks>
/// <para>Verifying and repairing hold whole blocks in memory: the search for moved data keeps about three blocks per
/// thread, a repair one block per rebuilt piece. With 7 GB blocks (2 blocks for 14 GB of files) that cannot work, and
/// arrays in .NET stop at 2 GB anyway. Such blocks are also of no use: any damage, even a single byte, costs a whole
/// block of recovery data.</para>
/// <para>Examples: 14 GB of files → at most 64 MiB blocks, so at least 224 blocks. 10 TB → 320 MiB blocks (10 TB ÷
/// 32,768), the size 32,768 blocks need.</para>
/// </remarks>
public static class BlockSizeLimit
{
    /// <summary>64 MiB.</summary>
    public const long Normal = 64L << 20;

    /// <summary>The block count the limit always allows: the most blocks PAR2 can have.</summary>
    public const int BlocksAlwaysAllowed = 32768;

    /// <summary>The largest block for files of <paramref name="totalBytes"/>, a multiple of <paramref name="multiple"/> (4 for PAR2, 8 for PAR3).</summary>
    public static long For(long totalBytes, int multiple)
    {
        long needed = (Math.Max(0, totalBytes) + BlocksAlwaysAllowed - 1) / BlocksAlwaysAllowed;
        return Math.Max(Normal, (needed + multiple - 1) / multiple * multiple);
    }

    /// <summary>Why a larger block is refused, in words for the user.</summary>
    public static string TooLarge(long limit)
        => $"Blocks can be at most {limit:N0} bytes for these files ({(limit == Normal ? "64 MiB" : "what 32,768 blocks need")}): verifying and repairing hold whole blocks in memory.";
}

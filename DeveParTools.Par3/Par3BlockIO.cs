using DeveParTools.Core.Codes;
using DeveParTools.Core.IO;

namespace DeveParTools.Par3;

/// <summary>Where the bytes of a piece can be read from (the original location, or wherever it was found).</summary>
public readonly record struct PieceSource(string Path, long Offset);

/// <summary>
/// Assembles input blocks from their pieces: a full block is read from one place, a block of packed tails is
/// assembled from several files (gaps and the end are zero).
/// </summary>
/// <remarks>
/// Example with a 100-byte block holding two packed tails:
/// <code>
/// block 7:  [ tail of a.txt (60 bytes) | tail of b.txt (30 bytes) | 0000000000 ]
///             offset 0                   offset 60                  zero padding
/// </code>
/// Reading bytes [50, 100) of block 7 reads a.txt's tail bytes 50..60, b.txt's tail bytes 0..30, then 10 zeros.
/// </remarks>
public sealed class Par3BlockSource : IRegionSource
{
    private readonly IReadOnlyList<(ulong BlockOffset, ulong Length, PieceSource Source)>[] _blocks;
    private readonly FileHandleCache _handles;
    private readonly Func<int, int>? _indexMap;

    public Par3BlockSource(IReadOnlyList<(ulong BlockOffset, ulong Length, PieceSource Source)>[] blocks, FileHandleCache handles, Func<int, int>? indexMap = null)
    {
        _blocks = blocks;
        _handles = handles;
        _indexMap = indexMap;
    }

    public IReadOnlyList<(ulong BlockOffset, ulong Length, PieceSource Source)>[] Blocks => _blocks;

    public FileHandleCache Handles => _handles;

    /// <summary>Optional per-read hook (e.g. progress or state updates).</summary>
    public Action<int>? OnRead { get; init; }

    public void Read(int index, long offset, Span<byte> destination)
    {
        int block = _indexMap?.Invoke(index) ?? index;
        destination.Clear();
        if (block < 0)
        {
            return;
        }

        ReadBlock(block, offset, destination);
        OnRead?.Invoke(block);
    }

    public void ReadBlock(int block, long offset, Span<byte> destination)
    {
        long end = offset + destination.Length;
        foreach (var (blockOffset, length, source) in _blocks[block])
        {
            long ps = (long)blockOffset;
            long pe = ps + (long)length;
            long s = Math.Max(ps, offset);
            long e = Math.Min(pe, end);
            if (s >= e)
            {
                continue;
            }

            _handles.ReadPadded(source.Path, source.Offset + (s - ps), destination.Slice((int)(s - offset), (int)(e - s)));
        }
    }
}

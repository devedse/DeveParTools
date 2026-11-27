namespace DeveParTools.Par3;

/// <summary>
/// First-fit packing of file tails into shared blocks: each tail goes into the first block (in the order they were
/// opened) that still has room for it, or opens a new block. Used for counting (<see cref="Par3Creator.CountBlocks"/>)
/// and for the real layout, so both always agree.
/// </summary>
/// <remarks>
/// <para>Looking through the open blocks one by one costs blocks × tails: a folder with 300,000 small files can open
/// tens of thousands of blocks, and the block size search counts the set some 30 times. So the largest free space of
/// every range of blocks is kept in a tree (a segment tree over the block numbers), and the first block with room is
/// found by walking down from the root, always taking the left half when it has room: O(log blocks) per tail.</para>
/// <code>
/// free space per block:      [10  0  45  3]          looking for room for 40:
/// tree (max of each range):        45                root 45 ≥ 40 → there is a block
///                              10       45           left half 10 &lt; 40 → go right
///                            10   0   45   3         45 ≥ 40 → block 2
/// </code>
/// <para>Blocks that do not exist yet count as having no room, so the tree can be larger than the number of blocks; it
/// doubles when it is full.</para>
/// </remarks>
internal sealed class FirstFitBlocks(ulong blockSize)
{
    private readonly List<ulong> _used = [];
    private ulong[] _tree = new ulong[32];
    private int _leaves = 16;

    /// <summary>Number of blocks opened.</summary>
    public int Count => _used.Count;

    /// <summary>Puts a tail of <paramref name="length"/> bytes (1 ≤ length ≤ block size) into a block.</summary>
    /// <returns>The block (0 = the first one opened) and the offset of the tail in it.</returns>
    public (int Block, ulong Offset) Place(ulong length)
    {
        int block = FirstWithRoom(length);
        if (block < 0)
        {
            block = _used.Count;
            _used.Add(0);
            if (block == _leaves)
            {
                Grow();
            }
        }

        ulong offset = _used[block];
        _used[block] = offset + length;
        Set(block, blockSize - _used[block]);
        return (block, offset);
    }

    private int FirstWithRoom(ulong length)
    {
        if (_tree[1] < length)
        {
            return -1;
        }

        int node = 1;
        while (node < _leaves)
        {
            node = _tree[2 * node] >= length ? 2 * node : 2 * node + 1;
        }

        return node - _leaves;
    }

    private void Set(int block, ulong free)
    {
        int node = block + _leaves;
        _tree[node] = free;
        for (node /= 2; node >= 1; node /= 2)
        {
            _tree[node] = Math.Max(_tree[2 * node], _tree[2 * node + 1]);
        }
    }

    private void Grow()
    {
        _leaves *= 2;
        _tree = new ulong[2 * _leaves];
        for (int i = 0; i < _used.Count; i++)
        {
            Set(i, blockSize - _used[i]);
        }
    }
}

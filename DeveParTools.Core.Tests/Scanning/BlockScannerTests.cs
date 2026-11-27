using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Scanning;

namespace DeveParTools.Core.Tests.Scanning;

[TestClass]
public sealed class BlockScannerTests
{
    private string _dir = "";

    [TestInitialize]
    public void CreateDirectory() => _dir = TestData.CreateTempDirectory();

    [TestCleanup]
    public void DeleteDirectory() => TestData.DeleteDirectory(_dir);

    [TestMethod]
    [DataRow(false, DisplayName = "CRC-64 rolling checksum")]
    [DataRow(true, DisplayName = "byte-sum rolling checksum: many false candidates, rejected by the strong hash")]
    public void FindsBlocksAtAnyOffset_AndTheTail(bool weakSum)
    {
        // garbage(37) reverse(B0) B0 B1 garbage(5) B2 B0 tail(100): every block is at an unexpected offset. The
        // reversed B0 has the same byte sum as B0, so with the byte-sum checksum it is a certain false candidate.
        var rnd = new Random(1);
        const int w = 256;
        var blocks = Enumerable.Range(0, 3).Select(_ => TestData.RandomBytes(rnd, w)).ToArray();
        var tail = TestData.RandomBytes(rnd, 100);
        var parts = new[] { TestData.RandomBytes(rnd, 37), blocks[0].Reverse().ToArray(), blocks[0], blocks[1], TestData.RandomBytes(rnd, 5), blocks[2], blocks[0], tail };
        var path = Write("a.bin", parts);
        var matcher = new TestMatcher(blocks, [(3, tail), (4, TestData.RandomBytes(rnd, 50))], weakSum: weakSum);
        using var handles = new FileHandleCache();
        var found = BlockScanner.Scan(path, matcher, handles, threads: 2);
        CollectionAssert.AreEqual(
            new[]
            {
                new FoundBlock(0, 37 + w, w),
                new FoundBlock(1, 37 + 2 * w, w),
                new FoundBlock(2, 42 + 3 * w, w),
                new FoundBlock(0, 42 + 4 * w, w),
                new FoundBlock(3, 42 + 5 * w, 100),
            },
            found);
        Assert.AreEqual(weakSum, matcher.RejectedCandidates > 0);
    }

    [TestMethod]
    public void GarbageAtTheEnd_IsSlidThroughToTheLastPosition()
    {
        // B0 then 3 windows' worth of garbage: the scan slides to the very last window start without a match.
        var rnd = new Random(5);
        const int w = 128;
        var block = TestData.RandomBytes(rnd, w);
        var path = Write("end.bin", [block, TestData.RandomBytes(rnd, 3 * w)]);
        var matcher = new TestMatcher([block], []);
        using var handles = new FileHandleCache();
        CollectionAssert.AreEqual(new[] { new FoundBlock(0, 0, w) }, BlockScanner.Scan(path, matcher, handles, threads: 1));
        Assert.AreEqual(2, matcher.WeakCalls, "one full computation per run of sliding (after the match the window is recomputed)");
        Assert.AreEqual(1 + 2 * w + 1, matcher.MayMatchCalls, "every window start is tested exactly once");
    }

    [TestMethod]
    public void FileShorterThanABlock_OnlyTriesTails()
    {
        var rnd = new Random(2);
        var content = TestData.RandomBytes(rnd, 60);
        var path = Write("short.bin", [content]);
        // Tail lengths that do not fit (0, longer than the file) are skipped; 60 matches, 30 does not.
        var matcher = new TestMatcher([TestData.RandomBytes(rnd, 128)], [(7, content), (8, content[..30]), (9, new byte[200])], extraTailLengths: [0]);
        using var handles = new FileHandleCache();
        var found = BlockScanner.Scan(path, matcher, handles, threads: 1);
        CollectionAssert.AreEqual(new[] { new FoundBlock(7, 0, 60) }, found);
        Assert.AreEqual(0, matcher.WeakCalls, "no full window exists");
    }

    [TestMethod]
    public void LargeFile_IsSplitIntoSegments_WithoutLosingBlocksAtTheBoundaries()
    {
        // Positions ≥ 32 MiB: two 16 MiB segments, each refilling its 4 MiB buffer several times. The segment
        // boundary falls inside a block; that block is reported once, by the segment it starts in.
        var rnd = new Random(3);
        const int w = 64 * 1024;
        var blocks = Enumerable.Range(0, 5).Select(_ => TestData.RandomBytes(rnd, w)).ToArray();
        int count = 513 + 7;
        var order = Enumerable.Range(0, count).Select(i => (i * 7 + i / 3) % blocks.Length).ToArray();
        var path = Write("large.bin", order.Select(i => blocks[i]).Prepend(new byte[] { 1, 2, 3 }).ToArray());
        var matcher = new TestMatcher(blocks, []);
        using var handles = new FileHandleCache();
        var found = BlockScanner.Scan(path, matcher, handles, threads: 4);
        CollectionAssert.AreEqual(order.Select((b, i) => new FoundBlock(b, 3 + (long)i * w, w)).ToArray(), found);
    }

    [TestMethod]
    public void FileThatShrinksDuringTheScan_StopsCleanly()
    {
        // The matcher truncates the file while the scan is in its first 4 MiB buffer; the refill then finds less
        // than one window and the segment ends there instead of matching stale data.
        var rnd = new Random(4);
        const int w = 64 * 1024;
        var block = TestData.RandomBytes(rnd, w);
        var path = Write("shrink.bin", Enumerable.Repeat(block, 80).ToArray());
        long shrinkTo = (4L << 20) + 100;
        var matcher = new TestMatcher([block], [])
        {
            OnMayMatch = calls =>
            {
                if (calls == 10)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fs.SetLength(shrinkTo);
                }
            },
        };
        using var handles = new FileHandleCache();
        var found = BlockScanner.Scan(path, matcher, handles, threads: 1);
        var expected = Enumerable.Range(0, (4 << 20) / w).Select(i => new FoundBlock(0, (long)i * w, w)).ToArray();
        CollectionAssert.AreEqual(expected, found, "only the blocks of the first buffer");
    }

    [TestMethod]
    public void Cancellation_Throws()
    {
        var path = Write("c.bin", [new byte[1000]]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var handles = new FileHandleCache();
        Assert.Throws<OperationCanceledException>(() => BlockScanner.Scan(path, new TestMatcher([new byte[100]], []), handles, 1, cts.Token));
    }

    private string Write(string name, byte[][] parts)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        foreach (var p in parts)
        {
            fs.Write(p);
        }

        return path;
    }

    /// <summary>PAR3-style matcher: CRC-64 as the rolling checksum, BLAKE3 as the strong hash.</summary>
    private sealed class TestMatcher : IBlockMatcher
    {
        private readonly RollingCrc64 _roll;
        private readonly bool _weakSum;
        private readonly Dictionary<ulong, List<(int Index, byte[] Hash)>> _full = [];
        private readonly (int Index, byte[] Data)[] _tails;
        private int _mayMatchCalls;
        private int _rejected;

        /// <param name="weakSum">Use the plain byte sum as the rolling checksum instead of CRC-64 (lots of collisions).</param>
        public TestMatcher(byte[][] blocks, (int Index, byte[] Data)[] tails, int[]? extraTailLengths = null, bool weakSum = false)
        {
            BlockSize = blocks[0].Length;
            _roll = new RollingCrc64(BlockSize);
            _weakSum = weakSum;
            for (int i = 0; i < blocks.Length; i++)
            {
                var weak = Weak(blocks[i]);
                if (!_full.TryGetValue(weak, out var list))
                {
                    _full[weak] = list = [];
                }

                list.Add((i, Blake3.Hash(blocks[i])));
            }

            WeakCalls = 0;

            _tails = tails;
            TailLengths = tails.Select(t => t.Data.Length).Concat(extraTailLengths ?? []).Distinct().ToArray();
        }

        public int BlockSize { get; }

        public int WeakCalls { get; private set; }

        public int MayMatchCalls => Volatile.Read(ref _mayMatchCalls);

        public Action<int>? OnMayMatch { get; init; }

        public IReadOnlyCollection<int> TailLengths { get; }

        public int RejectedCandidates => Volatile.Read(ref _rejected);

        public ulong Weak(ReadOnlySpan<byte> window)
        {
            WeakCalls++;
            if (!_weakSum)
            {
                return Crc64Iso.Compute(window);
            }

            ulong sum = 0;
            foreach (var b in window)
            {
                sum += b;
            }

            return sum;
        }

        public ulong Slide(ulong weak, byte outgoing, byte incoming) => _weakSum ? weak - outgoing + incoming : _roll.Slide(weak, outgoing, incoming);

        public bool MayMatch(ulong weak)
        {
            int calls = Interlocked.Increment(ref _mayMatchCalls);
            OnMayMatch?.Invoke(calls);
            return _full.ContainsKey(weak);
        }

        public int MatchFull(ulong weak, ReadOnlySpan<byte> window)
        {
            var hash = Blake3.Hash(window);
            foreach (var (index, h) in _full[weak])
            {
                if (h.AsSpan().SequenceEqual(hash))
                {
                    return index;
                }
            }

            Interlocked.Increment(ref _rejected);
            return -1;
        }

        public int MatchTail(ReadOnlySpan<byte> data)
        {
            foreach (var (index, d) in _tails)
            {
                if (d.AsSpan().SequenceEqual(data))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}

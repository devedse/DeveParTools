using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Core.Scanning;
using DeveParTools.Core.Util;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

public enum Par3FileStatus
{
    Complete,
    Damaged,
    Missing,
}

public sealed class Par3FileVerification
{
    public required Par3FileEntry File { get; init; }

    public required string TargetPath { get; init; }

    public Par3FileStatus Status { get; set; }
}

public sealed class Par3VerifyOptions
{
    public required string Par3Path { get; init; }

    public IReadOnlyList<string> ExtraPar3Files { get; init; } = [];

    public IReadOnlyList<string> ExtraDataFiles { get; init; } = [];

    public string? BaseDirectory { get; init; }
}

/// <summary>How to assemble an available block: pieces at block offsets (a single piece for whole blocks).</summary>
public sealed record BlockAvailability(IReadOnlyList<(ulong BlockOffset, ulong Length, PieceSource Source)> Pieces);

public sealed class Par3VerifyResult
{
    public required Par3InputSet Set { get; init; }

    public required string BaseDirectory { get; init; }

    public required IReadOnlyList<Par3FileVerification> Files { get; init; }

    /// <summary>Per input block: how it can be read, or null when missing.</summary>
    public required BlockAvailability?[] Blocks { get; init; }

    /// <summary>Found locations of tails (≥ 40 bytes), keyed by (file, chunk).</summary>
    public required Dictionary<(Par3FileEntry, Par3Chunk), PieceSource> Tails { get; init; }

    public required IReadOnlyList<string> MissingDirectories { get; init; }

    public int MissingBlocks => Blocks.Count(b => b == null);

    public int RecoveryBlocksAvailable => Set.RecoveryBlocks.Count;

    public bool AllFilesComplete => Files.All(f => f.Status == Par3FileStatus.Complete) && MissingDirectories.Count == 0;

    /// <summary>
    /// Whether the missing blocks can be rebuilt from the available recovery blocks. For FFT sets this is checked
    /// per cohort: a cohort only rebuilds its own blocks, so the total count is not enough.
    /// </summary>
    public bool RepairPossible => Par3Repairer.CanReconstruct(this);
}

/// <summary>Verifies the files of a PAR3 input set, locating every intact block and tail wherever it is.</summary>
public static class Par3Verifier
{
    public static Par3VerifyResult Verify(Par3VerifyOptions options, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var observer = context.Observer;
        var token = context.CancellationToken;

        var par3Files = Par3InputSet.DiscoverFiles(options.Par3Path).Concat(options.ExtraPar3Files.Select(Path.GetFullPath)).Distinct(StringComparer.Ordinal).ToList();
        long parBytes = par3Files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        observer.Plan(VerifyStages.Plan(parBytes, parBytes, 0));
        observer.Stage(VerifyStages.Loading);
        var reads = new Par3FileReadResult[par3Files.Count];
        // Progress: how far into each file reading has got (the files are read in parallel; big ones report as they go).
        var positions = new long[par3Files.Count];
        void Reached(int i, long position)
        {
            Volatile.Write(ref positions[i], position);
            observer.Progress(Math.Min(1, (double)positions.Sum() / Math.Max(1, parBytes)));
        }

        Parallel.For(0, par3Files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Threads), CancellationToken = token }, i =>
        {
            reads[i] = Par3PacketReader.ReadFile(par3Files[i], token, position => Reached(i, position), context.Threads);
            Reached(i, File.Exists(par3Files[i]) ? new FileInfo(par3Files[i]).Length : 0);
        });
        var set = Par3InputSet.Assemble(reads) ?? throw new InvalidDataException("No PAR3 Start and Root packets found; the input set cannot be used.");
        foreach (var w in set.Warnings)
        {
            observer.Log(ParLogLevel.Warning, w);
        }

        var baseDir = Path.GetFullPath(options.BaseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.Par3Path))!);
        int blockCount = (int)set.BlockCount;
        var map = context.CreateBlockMap(blockCount, set.RecoveryBlocks.Count);
        for (int i = 0; i < set.RecoveryBlocks.Count; i++)
        {
            map.SetRecovery(i, RecoveryBlockState.Available);
            map.RecoveryContainer[i] = Path.GetFileName(set.RecoveryBlocks[i].Data.SourceFile ?? string.Empty);
        }

        var files = new List<Par3FileVerification>();
        foreach (var f in set.Files)
        {
            string target = string.Empty;
            if (PathSafety.TryResolveUnder(baseDir, f.RelativePath, out var p, out var reason))
            {
                target = p;
            }
            else
            {
                observer.Log(ParLogLevel.Warning, $"Ignoring unsafe file name '{f.RelativePath}': {reason}");
            }

            files.Add(new Par3FileVerification { File = f, TargetPath = target, Status = Par3FileStatus.Missing });
        }

        var targets = files.ToDictionary(f => f.File, f => f.TargetPath);
        for (int b = 0; b < blockCount; b++)
        {
            var owners = set.BlockPieces[b];
            map.Ownership[b] = owners.Select(o => new BlockLocation(targets[o.File], (long)o.FileOffset, (int)o.Length)).ToArray();
            if (owners.Count(o => !o.IsTail) > 1)
            {
                map.AddFlags(b, BlockFlags.Deduplicated);
            }

            if (owners.Count(o => o.IsTail) > 1)
            {
                map.AddFlags(b, BlockFlags.TailPacked);
            }
        }

        var blocks = new BlockAvailability?[blockCount];
        var tails = new Dictionary<(Par3FileEntry, Par3Chunk), PieceSource>();
        var matcher = new Par3BlockMatcher(set);
        using var handles = new FileHandleCache();

        // Data packets inside the .par3 files (stored data): a source for every block they hold. The grid shows them
        // only for blocks that are not found in the files themselves, once those have been checked (below).
        var stored = new List<(int Index, Par3DataPacket Packet)>();
        foreach (var (index, packet) in set.DataPackets)
        {
            if (index < (ulong)blockCount)
            {
                blocks[index] = new BlockAvailability([(0, (ulong)packet.DataLength, new PieceSource(packet.SourceFile!, packet.DataOffset))]);
                stored.Add(((int)index, packet));
            }
        }

        long totalBytes = Math.Max(1, files.Where(f => File.Exists(f.TargetPath)).Sum(f => new FileInfo(f.TargetPath).Length));
        observer.Plan(VerifyStages.Plan(parBytes, totalBytes, 0));
        observer.Stage(VerifyStages.Verifying);
        // The files are checked in parallel (one per thread); progress is how far each one has got. Blocks and
        // tails can be shared between files (packed tails), so writing them is guarded by one lock.
        var present = files.Where(fv => fv.TargetPath.Length > 0 && File.Exists(fv.TargetPath)).ToList();
        var checkedUpTo = new long[present.Count];
        var sync = new Lock();

        // Each file is read in windows of whole blocks (see CheckInPlace); the files checked at once share the budget.
        long window = Math.Clamp(context.MemoryBudget / Math.Max(1, Math.Min(context.Threads, present.Count)), 4L << 20, MaxWindow);
        Parallel.For(0, present.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Threads), CancellationToken = token }, i =>
        {
            var fv = present[i];
            string name = Path.GetFileName(fv.TargetPath);
            void Reached(long position)
            {
                Volatile.Write(ref checkedUpTo[i], position);
                observer.Progress(Math.Min(1, (double)checkedUpTo.Sum() / totalBytes), name);
            }

            bool complete = CheckInPlace(fv, set, blocks, tails, handles, map, sync, Reached, window, context.Threads);
            fv.Status = complete ? Par3FileStatus.Complete : Par3FileStatus.Damaged;
            Reached(new FileInfo(fv.TargetPath).Length);
        });
        var damaged = present.Where(fv => fv.Status == Par3FileStatus.Damaged).Select(fv => fv.TargetPath).ToList();

        // Scan damaged files and extra files for blocks and tails anywhere.
        var toScan = damaged.Concat(options.ExtraDataFiles.Select(Path.GetFullPath).Where(File.Exists)).Distinct(StringComparer.Ordinal).ToList();
        if (toScan.Count > 0 && (blocks.Any(b => b == null) || tails.Count < CountTails(set)))
        {
            long scanBytes = Math.Max(1, toScan.Sum(f => new FileInfo(f).Length));
            long scannedBytes = 0;
            observer.Plan(VerifyStages.Plan(parBytes, totalBytes, scanBytes));
            observer.Stage(VerifyStages.Scanning);
            foreach (var path in toScan)
            {
                token.ThrowIfCancellationRequested();
                if (set.BlockSize <= int.MaxValue && blocks.Any(b => b == null))
                {
                    string name = Path.GetFileName(path);
                    foreach (var hit in BlockScanner.Scan(path, matcher, handles, context.Threads, token, n => observer.Progress((double)Interlocked.Add(ref scannedBytes, n) / scanBytes, name)))
                    {
                        foreach (int idx in matcher.SameContent(hit.BlockIndex))
                        {
                            if (blocks[idx] == null)
                            {
                                blocks[idx] = new BlockAvailability([(0, set.BlockSize, new PieceSource(path, hit.Offset))]);
                                bool inOwnFile = set.BlockPieces[idx].Any(o => string.Equals(targets[o.File], path, StringComparison.Ordinal));
                                map.Set(idx, inOwnFile ? BlockState.FoundMoved : BlockState.FoundElsewhere);
                                map.FoundAt[idx] = new BlockLocation(path, hit.Offset, (int)set.BlockSize);
                            }
                        }
                    }
                }

                ScanTails(path, set, tails, handles, token);
            }
        }

        foreach (var (index, packet) in stored.Where(d => map.Get(d.Index) == BlockState.Unknown))
        {
            map.Set(index, BlockState.FoundStored);
            map.FoundAt[index] = new BlockLocation(packet.SourceFile!, packet.DataOffset, (int)packet.DataLength);
        }

        // Blocks made of tails become available when all their tails are known: "found" when every tail is where its
        // file has it, "found elsewhere" when at least one came from the search.
        for (int b = 0; b < blockCount; b++)
        {
            if (blocks[b] != null)
            {
                continue;
            }

            var pieces = set.BlockPieces[b];
            if (pieces.Count > 0 && pieces.All(p => p.IsTail && tails.ContainsKey((p.File, p.Chunk))))
            {
                blocks[b] = new BlockAvailability(pieces.Select(p => (p.BlockOffset, p.Length, tails[(p.File, p.Chunk)])).ToList());
                bool inPlace = pieces.All(p => tails[(p.File, p.Chunk)] == new PieceSource(targets[p.File], (long)p.FileOffset));
                map.Set(b, inPlace ? BlockState.Found : BlockState.FoundElsewhere);
            }
        }

        var missingDirs = set.Directories.Select(d => PathSafety.TryResolveUnder(baseDir, d.RelativePath, out var dp, out _) ? dp : null)
            .Where(d => d != null && !Directory.Exists(d)).Select(d => d!).ToList();
        var result = new Par3VerifyResult { Set = set, BaseDirectory = baseDir, Files = files, Blocks = blocks, Tails = tails, MissingDirectories = missingDirs };
        bool possible = result.RepairPossible;
        for (int b = 0; b < blockCount; b++)
        {
            if (blocks[b] == null)
            {
                map.Set(b, possible ? BlockState.Recoverable : BlockState.Unrecoverable);
            }
        }

        observer.Log(result.AllFilesComplete ? ParLogLevel.Info : ParLogLevel.Warning,
            result.AllFilesComplete
                ? "All files are correct."
                : $"{result.MissingBlocks} block(s) missing, {result.RecoveryBlocksAvailable} recovery block(s) available: repair {(possible ? "is possible" : "is NOT possible")}.");
        return result;
    }

    private static int CountTails(Par3InputSet set) => set.Files.Sum(f => f.Chunks.Count(c => c.TailLength(set.BlockSize) >= Par3Chunk.InlineLimit));

    /// <summary>The most a file is read at once while it is checked.</summary>
    private const long MaxWindow = 64L << 20;

    /// <summary>Reads a file in chunk order, checking every block and tail at its expected position.</summary>
    /// <remarks>
    /// Full blocks are read in windows of whole blocks (up to <paramref name="window"/> bytes, e.g. 23 blocks of
    /// 2.69 MiB): the file's own hash gets the whole window at once, which lets BLAKE3 spread it over the cores, while
    /// the blocks of the window get their checksums in parallel. Block by block, one core did all three checksums of
    /// every byte, so a 7.3 GB file took 15 to 20 seconds, whatever the number of cores.
    /// </remarks>
    /// <param name="reached">Called now and then with how far into the file the check has got (bytes), for progress.</param>
    private static bool CheckInPlace(Par3FileVerification fv, Par3InputSet set, BlockAvailability?[] blocks, Dictionary<(Par3FileEntry, Par3Chunk), PieceSource> tails, FileHandleCache handles, BlockStateMap map, Lock sync, Action<long> reached, long window, int threads)
    {
        var f = fv.File;
        long actualLen = new FileInfo(fv.TargetPath).Length;
        ulong bs = set.BlockSize;
        var hasher = new Par3FileHasher(f);
        byte[]? buf = null;
        bool allPieces = true;
        var unverified = new List<(ulong Index, long Position)>();

        void Block(ulong idx, long pos, bool ok, ulong crc, Fingerprint fp)
        {
            if (ok && set.BlockChecksums[idx] == null)
            {
                // No External Data checksum (the specification allows that for blocks stored in Data packets):
                // the block is only known to be good if the whole file hash matches.
                unverified.Add((idx, pos));
            }
            else if (ok && set.BlockChecksums[idx] is { } expected && expected.Crc64 == crc && expected.Hash == fp)
            {
                lock (sync)
                {
                    blocks[idx] ??= new BlockAvailability([(0, bs, new PieceSource(fv.TargetPath, pos))]);
                }

                map.Set((int)idx, BlockState.Found);
            }
            else
            {
                allPieces = false;
                lock (sync)
                {
                    if (blocks[idx] == null)
                    {
                        map.Set((int)idx, BlockState.Damaged);
                    }
                }
            }
        }

        ulong offset = 0;
        foreach (var c in f.Chunks)
        {
            if (!c.Protected)
            {
                // Not covered by any block; it only counts in the file hash (see Par3FileHasher).
                hasher.Unprotected(c.Length);
                offset += c.Length;
                continue;
            }

            ulong full = c.FullBlocks(bs);
            if (bs <= (ulong)window)
            {
                CheckWindows(fv.TargetPath, c.FirstBlock, full, (long)offset, (long)bs, actualLen, (int)(window / (long)bs), handles, hasher, threads, Block, reached);
            }
            else
            {
                // Blocks larger than a window: one at a time, in pieces.
                buf ??= new byte[(int)Math.Min(bs, 1 << 24)];
                for (ulong b = 0; b < full; b++)
                {
                    long pos = (long)(offset + b * bs);
                    reached(pos);
                    bool ok = HashRange(fv.TargetPath, pos, (long)bs, handles, hasher, buf, out var crc, out var fp) && actualLen >= pos + (long)bs;
                    Block(c.FirstBlock + b, pos, ok, crc, fp);
                }
            }

            ulong tail = c.TailLength(bs);
            if (tail > 0)
            {
                long pos = (long)(offset + full * bs);
                var data = new byte[tail];
                int n = handles.ReadPadded(fv.TargetPath, pos, data);
                hasher.Protected(data.AsSpan(0, n));
                bool ok = n == (int)tail;
                if (tail < Par3Chunk.InlineLimit)
                {
                    ok &= data.AsSpan().SequenceEqual(c.InlineTail);
                }
                else
                {
                    ok &= Fingerprint.Compute(data) == c.TailHash;
                    if (ok)
                    {
                        lock (sync)
                        {
                            tails.TryAdd((f, c), new PieceSource(fv.TargetPath, pos));
                        }
                    }
                }

                allPieces &= ok;
            }

            offset += c.Length;
        }

        bool complete = allPieces && actualLen == (long)f.Length && hasher.Matches(f.Packet.FileHash);
        if (complete)
        {
            foreach (var (idx, pos) in unverified)
            {
                lock (sync)
                {
                    blocks[idx] ??= new BlockAvailability([(0, bs, new PieceSource(fv.TargetPath, pos))]);
                }

                map.Set((int)idx, BlockState.Found);
            }
        }

        return complete;
    }

    /// <summary>
    /// Checks <paramref name="count"/> full blocks from <paramref name="start"/> on, <paramref name="perWindow"/> at
    /// a time (see <see cref="CheckInPlace"/>), and reports each to <paramref name="block"/>, in order.
    /// </summary>
    private static void CheckWindows(string path, ulong firstBlock, ulong count, long start, long bs, long actualLen, int perWindow, FileHandleCache handles,
        Par3FileHasher hasher, int threads, Action<ulong, long, bool, ulong, Fingerprint> block, Action<long> reached)
    {
        if (count == 0)
        {
            return;
        }

        var data = GC.AllocateUninitializedArray<byte>((int)(Math.Min((ulong)perWindow, count) * (ulong)bs));
        var crcs = new ulong[perWindow];
        var hashes = new Fingerprint[perWindow];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) };
        for (ulong first = 0; first < count; first += (ulong)perWindow)
        {
            int n = (int)Math.Min((ulong)perWindow, count - first);
            long pos = start + (long)first * bs;
            reached(pos);
            var span = data.AsMemory(0, (int)(n * bs));
            int got = handles.ReadPadded(path, pos, span.Span);
            Parallel.Invoke(
                parallel,
                () => hasher.Protected(span.Span[..got]),
                () => Parallel.For(0, n, parallel, j =>
                {
                    var b = span.Span.Slice((int)(j * bs), (int)bs);
                    crcs[j] = Crc64Iso.Compute(b);
                    hashes[j] = Fingerprint.Compute(b);
                }));
            for (int j = 0; j < n; j++)
            {
                long blockPos = pos + j * bs;
                bool ok = got >= (j + 1) * bs && actualLen >= blockPos + bs;
                block(firstBlock + first + (ulong)j, blockPos, ok, crcs[j], hashes[j]);
            }
        }
    }

    private static bool HashRange(string path, long pos, long length, FileHandleCache handles, Par3FileHasher fileHasher, byte[] buf, out ulong crc, out Fingerprint fp)
    {
        crc = 0;
        var blockHasher = new Blake3Hasher();
        bool complete = true;
        for (long done = 0; done < length;)
        {
            int n = (int)Math.Min(length - done, buf.Length);
            int got = handles.ReadPadded(path, pos + done, buf.AsSpan(0, n));
            complete &= got == n;
            fileHasher.Protected(buf.AsSpan(0, got));
            blockHasher.Update(buf.AsSpan(0, n));
            crc = Crc64Iso.Compute(buf.AsSpan(0, n), crc);
            done += n;
        }

        var h = new byte[16];
        blockHasher.Finalize(h);
        fp = Fingerprint.FromBytes(h);
        return complete;
    }

    /// <summary>Finds tails (≥ 40 bytes) anywhere in a file: rolling CRC-64 over 40-byte windows, confirmed by BLAKE3.</summary>
    private static void ScanTails(string path, Par3InputSet set, Dictionary<(Par3FileEntry, Par3Chunk), PieceSource> tails, FileHandleCache handles, CancellationToken token)
    {
        var wanted = new Dictionary<ulong, List<(Par3FileEntry File, Par3Chunk Chunk)>>();
        foreach (var f in set.Files)
        {
            foreach (var c in f.Chunks)
            {
                if (c.TailLength(set.BlockSize) >= Par3Chunk.InlineLimit && !tails.ContainsKey((f, c)))
                {
                    if (!wanted.TryGetValue(c.TailCrc, out var l))
                    {
                        wanted[c.TailCrc] = l = new List<(Par3FileEntry, Par3Chunk)>();
                    }

                    l.Add((f, c));
                }
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        const int W = Par3Chunk.InlineLimit;
        long len = new FileInfo(path).Length;
        if (len < W)
        {
            return;
        }

        var roll = new RollingCrc64(W);
        var buf = new byte[1 << 22];
        long bufStart = 0;
        int bufLen = handles.ReadPadded(path, 0, buf);
        ulong crc = Crc64Iso.Compute(buf.AsSpan(0, W));
        for (long pos = 0; pos + W <= len; pos++)
        {
            if ((pos & 0xFFFFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            if (wanted.TryGetValue(crc, out var candidates))
            {
                foreach (var (file, chunk) in candidates.ToList())
                {
                    ulong tailLen = chunk.TailLength(set.BlockSize);
                    if (pos + (long)tailLen > len || tails.ContainsKey((file, chunk)))
                    {
                        continue;
                    }

                    var data = new byte[tailLen];
                    handles.ReadPadded(path, pos, data);
                    if (Fingerprint.Compute(data) == chunk.TailHash)
                    {
                        tails[(file, chunk)] = new PieceSource(path, pos);
                    }
                }
            }

            if (pos + W >= len)
            {
                break;
            }

            int rel = (int)(pos - bufStart);
            if (rel + W >= bufLen)
            {
                bufStart = pos;
                bufLen = handles.ReadPadded(path, bufStart, buf);
                rel = 0;
            }

            crc = roll.Slide(crc, buf[rel], buf[rel + W]);
        }
    }
}

/// <summary>Full-block matching for PAR3: CRC-64-ISO rolling hash + BLAKE3 fingerprint.</summary>
internal sealed class Par3BlockMatcher : IBlockMatcher
{
    private readonly Dictionary<ulong, List<int>> _byCrc = new();
    private readonly Dictionary<BlockChecksum, List<int>> _groups = new();
    private readonly BlockChecksum?[] _checks;
    private readonly ulong[] _filter = new ulong[1 << 14];
    private readonly RollingCrc64 _roll;

    public Par3BlockMatcher(Par3InputSet set)
    {
        BlockSize = (int)Math.Min(set.BlockSize, int.MaxValue);
        _roll = new RollingCrc64(BlockSize);
        _checks = set.BlockChecksums;
        for (int i = 0; i < _checks.Length; i++)
        {
            if (_checks[i] is not { } c)
            {
                continue;
            }

            if (!_groups.TryGetValue(c, out var g))
            {
                _groups[c] = g = new List<int>();
                if (!_byCrc.TryGetValue(c.Crc64, out var l))
                {
                    _byCrc[c.Crc64] = l = new List<int>();
                }

                l.Add(i);
                _filter[(c.Crc64 & 0xFFFFF) >> 6] |= 1UL << (int)(c.Crc64 & 63);
            }

            g.Add(i);
        }
    }

    public int BlockSize { get; }

    public IReadOnlyCollection<int> TailLengths => [];

    public ulong Weak(ReadOnlySpan<byte> window) => Crc64Iso.Compute(window);

    public ulong Slide(ulong weak, byte outgoing, byte incoming) => _roll.Slide(weak, outgoing, incoming);

    public bool MayMatch(ulong weak) => (_filter[(weak & 0xFFFFF) >> 6] & (1UL << (int)(weak & 63))) != 0;

    public int MatchFull(ulong weak, ReadOnlySpan<byte> window)
    {
        if (!_byCrc.TryGetValue(weak, out var candidates))
        {
            return -1;
        }

        var fp = Fingerprint.Compute(window);
        foreach (var idx in candidates)
        {
            if (_checks[idx]!.Value.Hash == fp)
            {
                return idx;
            }
        }

        return -1;
    }

    public int MatchTail(ReadOnlySpan<byte> data) => -1;

    public IReadOnlyList<int> SameContent(int index) => _groups[_checks[index]!.Value];
}

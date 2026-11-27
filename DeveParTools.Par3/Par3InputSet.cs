using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

/// <summary>A piece of a file stored in an input block.</summary>
/// <param name="File">The file the piece belongs to.</param>
/// <param name="FileOffset">Offset of the piece in the file.</param>
/// <param name="Length">Length in bytes.</param>
/// <param name="BlockOffset">Offset inside the input block (non-zero for packed tails).</param>
/// <param name="IsTail">True for a chunk tail (identified by its own hash), false for a full block.</param>
public sealed record Par3Piece(Par3FileEntry File, ulong FileOffset, ulong Length, ulong BlockOffset, bool IsTail, Par3Chunk Chunk);

public sealed class Par3FileEntry
{
    public required string RelativePath { get; init; }

    public required Par3FilePacket Packet { get; init; }

    public required IReadOnlyList<Par3Chunk> Chunks { get; init; }

    public ulong Length => (ulong)Chunks.Sum(c => (decimal)c.Length);

    public IReadOnlyList<Par3Packet> Options { get; init; } = [];
}

public sealed class Par3DirectoryEntry
{
    public required string RelativePath { get; init; }

    public IReadOnlyList<Par3Packet> Options { get; init; } = [];
}

public sealed class Par3LinkEntry
{
    public required string RelativePath { get; init; }

    public required Par3LinkPacket Packet { get; init; }
}

/// <summary>A recovery block available in the .par3 files.</summary>
public sealed record Par3RecoveryBlock(Par3MatrixPacket Matrix, ulong Index, Par3RecoveryDataPacket Data, ulong CoveredBlocks);

/// <summary>
/// Everything known about one PAR3 input set: the file tree, how files map onto input blocks, the block checksums,
/// stored data blocks and recovery blocks.
/// </summary>
public sealed class Par3InputSet
{
    public required InputSetId SetId { get; init; }

    public required Par3StartPacket Start { get; init; }

    public required Par3RootPacket Root { get; init; }

    public ulong BlockSize => Start.BlockSize;

    public required List<Par3FileEntry> Files { get; init; }

    public required List<Par3DirectoryEntry> Directories { get; init; }

    public required List<Par3LinkEntry> Links { get; init; }

    public required ulong BlockCount { get; init; }

    /// <summary>Checksums of input blocks (from External Data packets); null when unknown.</summary>
    public required BlockChecksum?[] BlockChecksums { get; init; }

    /// <summary>Input blocks stored inside the PAR3 files (Data packets).</summary>
    public required Dictionary<ulong, Par3DataPacket> DataPackets { get; init; }

    public required List<Par3RecoveryBlock> RecoveryBlocks { get; init; }

    /// <summary>Pieces of files stored in each input block.</summary>
    public required List<Par3Piece>[] BlockPieces { get; init; }

    public List<string> Creators { get; } = new();

    public List<string> Comments { get; } = new();

    public IReadOnlyList<string> SourceFiles { get; init; } = [];

    public int DamagedPackets { get; init; }

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Assembles the input set from packets of one or more files. Picks the set whose Root packet is present; when
    /// several are present, the one that is not the parent of another (the newest incremental backup).
    /// </summary>
    public static Par3InputSet? Assemble(IReadOnlyList<Par3FileReadResult> files)
    {
        var all = files.SelectMany(f => f.Packets).ToList();
        var starts = new Dictionary<InputSetId, Par3StartPacket>();
        foreach (var s in all.OfType<Par3StartPacket>())
        {
            starts.TryAdd(s.SetId, s);
        }

        var roots = all.OfType<Par3RootPacket>().GroupBy(r => r.SetId).ToDictionary(g => g.Key, g => g.First());
        var candidates = roots.Keys.Where(starts.ContainsKey).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var parents = new HashSet<InputSetId>(starts.Values.Select(s => s.ParentSetId));
        var setId = candidates.OrderBy(c => parents.Contains(c) ? 1 : 0).ThenByDescending(c => all.Count(p => p.SetId == c)).First();
        var start = starts[setId];
        var root = roots[setId];

        // The set plus its ancestors (incremental backups reuse parent packets).
        var lineage = new List<InputSetId> { setId };
        var cur = start;
        while (cur.ParentSetId.Value != 0 && starts.TryGetValue(cur.ParentSetId, out var parent) && !lineage.Contains(cur.ParentSetId))
        {
            lineage.Add(cur.ParentSetId);
            cur = parent;
        }

        var lineageSet = new HashSet<InputSetId>(lineage);
        var packets = all.Where(p => lineageSet.Contains(p.SetId)).ToList();
        var byChecksum = new Dictionary<Fingerprint, Par3Packet>();
        foreach (var p in packets)
        {
            byChecksum.TryAdd(p.Checksum, p);
        }

        ulong blockSize = start.BlockSize;
        var warnings = new List<string>();
        var fileEntries = new List<Par3FileEntry>();
        var dirEntries = new List<Par3DirectoryEntry>();
        var linkEntries = new List<Par3LinkEntry>();

        List<Par3Packet> ResolveOptions(IReadOnlyList<Fingerprint> options)
        {
            var list = new List<Par3Packet>();
            foreach (var o in options)
            {
                if (byChecksum.TryGetValue(o, out var op))
                {
                    if (op is Par3LinkPacket link)
                    {
                        continue;
                    }

                    list.Add(op);
                }
                else
                {
                    warnings.Add($"Option packet {o} not found.");
                }
            }

            return list;
        }

        void Walk(string prefix, IReadOnlyList<Fingerprint> children, IReadOnlyList<Fingerprint> options, int depth)
        {
            if (depth > 256)
            {
                throw new InvalidDataException("Directory tree too deep (cycle?).");
            }

            foreach (var o in options)
            {
                if (byChecksum.TryGetValue(o, out var op) && op is Par3LinkPacket link)
                {
                    linkEntries.Add(new Par3LinkEntry { RelativePath = Join(prefix, link.Name), Packet = link });
                }
            }

            foreach (var c in children)
            {
                if (!byChecksum.TryGetValue(c, out var child))
                {
                    warnings.Add($"File/Directory packet {c} under '{prefix}' not found.");
                    continue;
                }

                switch (child)
                {
                    case Par3FilePacket f:
                        fileEntries.Add(new Par3FileEntry
                        {
                            RelativePath = Join(prefix, f.Name),
                            Packet = f,
                            Chunks = Par3Chunk.ParseAll(f.ChunkBytes, blockSize),
                            Options = ResolveOptions(f.Options),
                        });
                        break;
                    case Par3DirectoryPacket d:
                    {
                        var path = Join(prefix, d.Name);
                        dirEntries.Add(new Par3DirectoryEntry { RelativePath = path, Options = ResolveOptions(d.Options) });
                        Walk(path, d.Children, d.Options, depth + 1);
                        break;
                    }
                }
            }
        }

        Walk(string.Empty, root.Children, root.Options, 0);

        // Block count: the root's lowest unused index, but at least every referenced block.
        ulong maxIndex = root.LowestUnusedBlock;
        foreach (var f in fileEntries)
        {
            foreach (var c in f.Chunks.Where(c => c.Protected))
            {
                ulong full = c.FullBlocks(blockSize);
                if (full > 0)
                {
                    maxIndex = Math.Max(maxIndex, c.FirstBlock + full);
                }

                if (c.TailLength(blockSize) >= Par3Chunk.InlineLimit)
                {
                    maxIndex = Math.Max(maxIndex, c.TailBlock + 1);
                }
            }
        }

        if (maxIndex > int.MaxValue)
        {
            throw new NotSupportedException("Input sets with more than 2^31 blocks are not supported.");
        }

        int count = (int)maxIndex;
        var checksums = new BlockChecksum?[count];
        foreach (var e in packets.OfType<Par3ExternalDataPacket>())
        {
            for (int i = 0; i < e.Checksums.Count; i++)
            {
                ulong idx = e.FirstBlockIndex + (ulong)i;
                if (idx < maxIndex)
                {
                    checksums[idx] ??= e.Checksums[i];
                }
            }
        }

        var dataPackets = new Dictionary<ulong, Par3DataPacket>();
        foreach (var d in packets.OfType<Par3DataPacket>())
        {
            if (d.DataLength <= (long)blockSize)
            {
                dataPackets.TryAdd(d.BlockIndex, d);
            }
        }

        // Pieces per block.
        var pieces = new List<Par3Piece>[count];
        for (int i = 0; i < count; i++)
        {
            pieces[i] = new List<Par3Piece>();
        }

        foreach (var f in fileEntries)
        {
            ulong offset = 0;
            foreach (var c in f.Chunks)
            {
                if (c.Protected)
                {
                    ulong full = c.FullBlocks(blockSize);
                    for (ulong b = 0; b < full; b++)
                    {
                        pieces[c.FirstBlock + b].Add(new Par3Piece(f, offset + b * blockSize, blockSize, 0, false, c));
                    }

                    ulong tail = c.TailLength(blockSize);
                    if (tail >= Par3Chunk.InlineLimit)
                    {
                        pieces[c.TailBlock].Add(new Par3Piece(f, offset + full * blockSize, tail, c.TailOffset, true, c));
                    }
                }

                offset += c.Length;
            }
        }

        // Recovery blocks: (matrix packet, index) → data; the root checksum determines which inputs it covers.
        var rootCoverage = all.OfType<Par3RootPacket>().GroupBy(r => r.Checksum).ToDictionary(g => g.Key, g => g.First().LowestUnusedBlock);
        var matrices = packets.OfType<Par3MatrixPacket>().GroupBy(m => m.Checksum).ToDictionary(g => g.Key, g => g.First());
        var recovery = new List<Par3RecoveryBlock>();
        var seen = new HashSet<(Fingerprint, ulong)>();
        foreach (var r in packets.OfType<Par3RecoveryDataPacket>())
        {
            if (!matrices.TryGetValue(r.MatrixChecksum, out var m) || r.DataLength > (long)blockSize)
            {
                continue;
            }

            if (!rootCoverage.TryGetValue(r.RootChecksum, out var covered))
            {
                continue;
            }

            if (seen.Add((r.MatrixChecksum, r.RecoveryIndex)))
            {
                recovery.Add(new Par3RecoveryBlock(m, r.RecoveryIndex, r, covered));
            }
        }

        var set = new Par3InputSet
        {
            SetId = setId,
            Start = start,
            Root = root,
            Files = fileEntries,
            Directories = dirEntries,
            Links = linkEntries,
            BlockCount = maxIndex,
            BlockChecksums = checksums,
            DataPackets = dataPackets,
            RecoveryBlocks = recovery,
            BlockPieces = pieces,
            SourceFiles = files.Select(f => f.Path).ToList(),
            DamagedPackets = files.Sum(f => f.DamagedPackets),
        };
        set.Warnings.AddRange(warnings);
        set.Creators.AddRange(packets.OfType<Par3CreatorPacket>().Select(c => c.Text).Distinct());
        set.Comments.AddRange(packets.OfType<Par3CommentPacket>().Select(c => c.Text).Distinct());
        return set;
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "/" + name;

    /// <summary>The .par3 files belonging to <paramref name="par3Path"/>: siblings with the same base name.</summary>
    public static List<string> DiscoverFiles(string par3Path)
    {
        var full = Path.GetFullPath(par3Path);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        string baseName = name.EndsWith(".par3", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        foreach (var marker in new[] { ".vol", ".part" })
        {
            int i = baseName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i > 0)
            {
                baseName = baseName[..i];
            }
        }

        var result = new List<string> { full };
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var fn = Path.GetFileName(f);
                if (fn.EndsWith(".par3", StringComparison.OrdinalIgnoreCase) && !string.Equals(f, full, StringComparison.Ordinal)
                    && fn.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(f);
                }
            }
        }

        return result;
    }
}

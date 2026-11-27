using DeveParTools.Par2.Packets;

namespace DeveParTools.Par2;

/// <summary>One file of a PAR2 recovery set, assembled from its packets.</summary>
public sealed class Par2SetFile
{
    public required Md5Hash FileId { get; init; }

    /// <summary>Stored relative name ('/'-separated). Empty when the description packet is missing.</summary>
    public string Name { get; set; } = string.Empty;

    public long Length { get; set; } = -1;

    public Md5Hash FileHash { get; set; }

    public Md5Hash Hash16k { get; set; }

    public bool HasDescription { get; set; }

    /// <summary>MD5/CRC32 of each slice (zero-padded last slice), or null when the IFSC packet is missing.</summary>
    public IReadOnlyList<(Md5Hash Md5, uint Crc32)>? SliceChecksums { get; set; }

    /// <summary>Index of this file's first slice within the whole set.</summary>
    public int FirstSlice { get; set; }

    public int SliceCount { get; set; }

    public bool InRecoverySet { get; init; }
}

/// <summary>A PAR2 recovery set: all information gathered from the .par2 files.</summary>
public sealed class Par2RecoverySet
{
    public required Md5Hash SetId { get; init; }

    public required long SliceSize { get; init; }

    /// <summary>Recovery-set files in main-packet order (which defines slice numbering).</summary>
    public required IReadOnlyList<Par2SetFile> Files { get; init; }

    public IReadOnlyList<Par2SetFile> NonRecoveryFiles { get; init; } = [];

    /// <summary>Recovery slices by exponent (first intact copy wins).</summary>
    public SortedDictionary<uint, Par2RecoverySlicePacket> RecoverySlices { get; } = new();

    public List<string> Creators { get; } = new();

    public List<string> Comments { get; } = new();

    public int TotalSlices { get; init; }

    public IReadOnlyList<string> SourceFiles { get; init; } = [];

    public int DamagedPackets { get; init; }

    /// <summary>
    /// Builds a recovery set from packets read from one or more .par2 files (the file the user named first).
    /// </summary>
    /// <remarks>
    /// <para><b>Which set.</b> Files with the same base name can hold packets of other recovery sets (e.g. "set.par2"
    /// next to "set.old.par2"). Like par2cmdline, the set of the first packet read wins, i.e. the set of the file the
    /// user named; packets of sets without a main packet are skipped for that choice, so a stray packet cannot
    /// select a set that cannot be used. Returns null when no main packet exists at all.</para>
    /// <para><b>Duplicates.</b> Critical packets are repeated in every volume file. The first intact copy of each
    /// packet is used, except for a slice checksum packet whose entry count does not match the file length (which
    /// only a broken client writes): it is ignored, as par2cmdline does, and the file is then verified by its
    /// whole-file MD5 alone.</para>
    /// </remarks>
    public static Par2RecoverySet? Assemble(IReadOnlyList<Par2FileReadResult> files)
    {
        var all = files.SelectMany(f => f.Packets).ToList();
        var mains = new Dictionary<Md5Hash, Par2MainPacket>();
        foreach (var m in all.OfType<Par2MainPacket>())
        {
            mains.TryAdd(m.SetId, m);
        }

        var first = all.FirstOrDefault(p => mains.ContainsKey(p.SetId));
        if (first == null)
        {
            return null;
        }

        var setId = first.SetId;
        var main = mains[setId];
        var packets = all.Where(p => p.SetId == setId).ToList();

        var descriptions = new Dictionary<Md5Hash, Par2FileDescriptionPacket>();
        foreach (var d in packets.OfType<Par2FileDescriptionPacket>())
        {
            descriptions.TryAdd(d.FileId, d);
        }

        var checksums = new Dictionary<Md5Hash, Par2SliceChecksumPacket>();
        foreach (var c in packets.OfType<Par2SliceChecksumPacket>())
        {
            bool fits = !descriptions.TryGetValue(c.FileId, out var d) || c.Slices.Count == (d.Length + main.SliceSize - 1) / main.SliceSize;
            if (fits)
            {
                checksums.TryAdd(c.FileId, c);
            }
        }

        var unicodeNames = new Dictionary<Md5Hash, string>();
        foreach (var u in packets.OfType<Par2UnicodeFilenamePacket>())
        {
            unicodeNames.TryAdd(u.FileId, u.Name);
        }

        Par2SetFile Build(Md5Hash id, bool recovery)
        {
            var f = new Par2SetFile { FileId = id, InRecoverySet = recovery };
            if (descriptions.TryGetValue(id, out var d))
            {
                f.HasDescription = true;
                f.Name = unicodeNames.TryGetValue(id, out var u) ? u : d.Name;
                f.Length = d.Length;
                f.FileHash = d.FileHash;
                f.Hash16k = d.Hash16k;
            }

            if (checksums.TryGetValue(id, out var c))
            {
                f.SliceChecksums = c.Slices;
            }

            return f;
        }

        var recoveryFiles = main.RecoveryFileIds.Select(id => Build(id, true)).ToList();
        int slice = 0;
        foreach (var f in recoveryFiles)
        {
            f.FirstSlice = slice;
            if (f.Length >= 0)
            {
                f.SliceCount = (int)((f.Length + main.SliceSize - 1) / main.SliceSize);
            }
            else if (f.SliceChecksums != null)
            {
                f.SliceCount = f.SliceChecksums.Count;
            }

            slice += f.SliceCount;
        }

        var set = new Par2RecoverySet
        {
            SetId = setId,
            SliceSize = main.SliceSize,
            Files = recoveryFiles,
            NonRecoveryFiles = main.NonRecoveryFileIds.Select(id => Build(id, false)).ToList(),
            TotalSlices = slice,
            SourceFiles = files.Select(f => f.Path).ToList(),
            DamagedPackets = files.Sum(f => f.DamagedPackets),
        };

        foreach (var r in packets.OfType<Par2RecoverySlicePacket>())
        {
            if (r.DataLength == main.SliceSize)
            {
                set.RecoverySlices.TryAdd(r.Exponent, r);
            }
        }

        set.Creators.AddRange(packets.OfType<Par2CreatorPacket>().Select(c => c.Text).Distinct());
        set.Comments.AddRange(packets.OfType<Par2CommentPacket>().Select(c => c.Text).Distinct());
        return set;
    }

    /// <summary>
    /// Finds the .par2 files belonging to <paramref name="par2Path"/>: the file itself first, then every
    /// "basename.*.par2" sibling (case-insensitive) in name order, as par2cmdline does.
    /// </summary>
    /// <remarks>
    /// The base name is the file name without ".par2" and without a volume part ".volNN+MM" / ".volNN-MM" (the part
    /// after the last dot must be "vol", digits, an optional '+' or '-' and digits, matched like par2cmdline):
    /// <code>
    /// set.par2              → set     (finds set.vol00+01.par2, set.vol01+02.par2, ...)
    /// set.vol07+08.PAR2     → set     (finds set.par2 too)
    /// my.volume.par2        → my.volume
    /// </code>
    /// </remarks>
    public static List<string> DiscoverFiles(string par2Path)
    {
        var full = Path.GetFullPath(par2Path);
        var dir = Path.GetDirectoryName(full)!;
        var baseName = BaseNameOf(Path.GetFileName(full));
        var result = new List<string> { full };
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
            {
                var fn = Path.GetFileName(f);
                if (fn.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) && !string.Equals(f, full, StringComparison.Ordinal)
                    && fn.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(f);
                }
            }
        }

        return result;
    }

    /// <summary>"set.vol03+04.par2" → "set"; see <see cref="DiscoverFiles"/>.</summary>
    internal static string BaseNameOf(string fileName)
    {
        var name = fileName.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) ? fileName[..^5] : fileName;
        int dot = name.LastIndexOf('.');
        return dot >= 0 && IsVolumeTag(name.AsSpan(dot + 1)) ? name[..dot] : name;
    }

    /// <summary>"vol" + digits + optional ('+' or '-') + digits, case-insensitive: "vol0+1", "VOL12-3", "vol7".</summary>
    private static bool IsVolumeTag(ReadOnlySpan<char> tail)
    {
        if (!tail.StartsWith("vol", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool separatorSeen = false;
        foreach (var c in tail[3..])
        {
            if (c is '+' or '-' && !separatorSeen)
            {
                separatorSeen = true;
            }
            else if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

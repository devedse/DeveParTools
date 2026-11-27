using System.Security.Cryptography;
using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Core.Scanning;
using DeveParTools.Core.Util;
using DeveParTools.Par2.Packets;

namespace DeveParTools.Par2;

public enum Par2FileStatus
{
    /// <summary>File exists and matches its MD5.</summary>
    Complete,

    /// <summary>File exists but is damaged (wrong size or contents).</summary>
    Damaged,

    /// <summary>File does not exist.</summary>
    Missing,

    /// <summary>The file description packet is missing, so the file cannot be identified.</summary>
    Unknown,
}

public sealed class Par2FileVerification
{
    public required Par2SetFile File { get; init; }

    /// <summary>Where the file should be (empty when the name is unknown or unsafe).</summary>
    public required string TargetPath { get; init; }

    public Par2FileStatus Status { get; set; }

    /// <summary>Number of this file's slices that were found somewhere.</summary>
    public int SlicesFound { get; set; }

    /// <summary>A complete copy found under another name, if any.</summary>
    public string? FoundAs { get; set; }
}

/// <summary>Where good data for an input slice was found.</summary>
public readonly record struct SliceLocation(string Path, long Offset, int Length);

public sealed class Par2VerifyOptions
{
    /// <summary>The .par2 file to start from; its siblings (same base name) are loaded too.</summary>
    public required string Par2Path { get; init; }

    /// <summary>Additional .par2 files.</summary>
    public IReadOnlyList<string> ExtraPar2Files { get; init; } = [];

    /// <summary>Other files to search for data (renamed files, partial downloads, ...).</summary>
    public IReadOnlyList<string> ExtraDataFiles { get; init; } = [];

    /// <summary>Directory the stored file names are relative to (default: directory of the .par2 file).</summary>
    public string? BaseDirectory { get; init; }
}

public sealed class Par2VerifyResult
{
    public required Par2RecoverySet Set { get; init; }

    public required string BaseDirectory { get; init; }

    public required IReadOnlyList<Par2FileVerification> Files { get; init; }

    /// <summary>Per input slice: where intact data exists, or null when missing.</summary>
    public required SliceLocation?[] SliceSources { get; init; }

    public int MissingSlices => SliceSources.Count(s => s == null);

    public int RecoverySlicesAvailable => Set.RecoverySlices.Count;

    /// <summary>All critical information (descriptions, checksums) is present.</summary>
    public bool CriticalDataComplete => Set.Files.All(f => f.HasDescription && f.SliceChecksums != null);

    public bool AllFilesComplete => Files.All(f => f.Status == Par2FileStatus.Complete);

    public bool RepairPossible => CriticalDataComplete && MissingSlices <= RecoverySlicesAvailable;
}

/// <summary>Verifies the files of a PAR2 recovery set and locates every intact slice, wherever it is.</summary>
public static class Par2Verifier
{
    public static Par2VerifyResult Verify(Par2VerifyOptions options, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var observer = context.Observer;
        var token = context.CancellationToken;

        observer.Stage("Loading");
        var par2Files = Par2RecoverySet.DiscoverFiles(options.Par2Path).Concat(options.ExtraPar2Files.Select(Path.GetFullPath)).Distinct(StringComparer.Ordinal).ToList();
        var reads = new Par2FileReadResult[par2Files.Count];
        Parallel.For(0, par2Files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Threads), CancellationToken = token },
            i => reads[i] = Par2PacketReader.ReadFile(par2Files[i], token));
        var set = Par2RecoverySet.Assemble(reads)
            ?? throw new InvalidDataException("No PAR2 main packet found; the recovery set cannot be used.");
        foreach (var creator in set.Creators)
        {
            observer.Log(ParLogLevel.Info, $"Creator: {creator}");
        }

        var baseDir = Path.GetFullPath(options.BaseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.Par2Path))!);
        var map = context.CreateBlockMap(set.TotalSlices, set.RecoverySlices.Count);
        int rIdx = 0;
        foreach (var r in set.RecoverySlices.Values)
        {
            map.SetRecovery(rIdx, RecoveryBlockState.Available);
            map.RecoveryContainer[rIdx] = Path.GetFileName(r.DataFile);
            rIdx++;
        }

        var files = new List<Par2FileVerification>();
        foreach (var f in set.Files)
        {
            string target = string.Empty;
            if (f.HasDescription)
            {
                if (PathSafety.TryResolveUnder(baseDir, f.Name, out var p, out var reason))
                {
                    target = p;
                }
                else
                {
                    observer.Log(ParLogLevel.Warning, $"Ignoring unsafe file name '{f.Name}': {reason}");
                }
            }

            files.Add(new Par2FileVerification { File = f, TargetPath = target, Status = f.HasDescription ? Par2FileStatus.Missing : Par2FileStatus.Unknown });
            for (int s = 0; s < f.SliceCount; s++)
            {
                long off = s * set.SliceSize;
                map.Ownership[f.FirstSlice + s] = [new BlockLocation(target, off, (int)Math.Min(set.SliceSize, Math.Max(0, f.Length - off)))];
            }
        }

        var sources = new SliceLocation?[set.TotalSlices];
        var matcher = new Par2SliceMatcher(set);
        using var handles = new FileHandleCache();

        // Pass 1: every target file, slices at their expected positions; full scan only when damaged.
        observer.Stage("Verifying");
        long totalBytes = Math.Max(1, files.Where(f => File.Exists(f.TargetPath)).Sum(f => new FileInfo(f.TargetPath).Length));
        long doneBytes = 0;
        var scanThreads = Math.Max(1, context.Threads);
        foreach (var fv in files)
        {
            token.ThrowIfCancellationRequested();
            if (fv.TargetPath.Length == 0 || !File.Exists(fv.TargetPath))
            {
                continue;
            }

            long len = new FileInfo(fv.TargetPath).Length;
            map.SetRange(fv.File.FirstSlice, fv.File.SliceCount, BlockState.Scanning);
            bool complete = CheckInPlace(fv, set, matcher, sources, handles, map);
            if (complete)
            {
                fv.Status = Par2FileStatus.Complete;
            }
            else
            {
                fv.Status = Par2FileStatus.Damaged;
                foreach (var hit in BlockScanner.Scan(fv.TargetPath, matcher, handles, scanThreads, token))
                {
                    Record(hit, fv.TargetPath, set, matcher, sources, map, files);
                }
            }

            observer.Progress((double)Interlocked.Add(ref doneBytes, len) / totalBytes, Path.GetFileName(fv.TargetPath));
        }

        // Pass 2: extra files (other names, partial copies).
        foreach (var extra in options.ExtraDataFiles.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(extra) || files.Any(f => string.Equals(f.TargetPath, extra, StringComparison.Ordinal)))
            {
                continue;
            }

            observer.Log(ParLogLevel.Info, $"Scanning extra file {extra}");
            foreach (var hit in BlockScanner.Scan(extra, matcher, handles, scanThreads, token))
            {
                Record(hit, extra, set, matcher, sources, map, files);
            }

            // A complete copy of a damaged/missing file under another name?
            long len = new FileInfo(extra).Length;
            foreach (var fv in files.Where(f => f.Status != Par2FileStatus.Complete && f.File.Length == len && f.FoundAs == null))
            {
                if (Md5OfFile(extra) == fv.File.FileHash)
                {
                    fv.FoundAs = extra;
                }
            }
        }

        // Final per-slice and per-file state.
        foreach (var fv in files)
        {
            fv.SlicesFound = 0;
            for (int s = 0; s < fv.File.SliceCount; s++)
            {
                int idx = fv.File.FirstSlice + s;
                if (sources[idx] != null)
                {
                    fv.SlicesFound++;
                }
            }
        }

        var result = new Par2VerifyResult { Set = set, BaseDirectory = baseDir, Files = files, SliceSources = sources };
        bool possible = result.RepairPossible;
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] == null)
            {
                map.Set(i, possible ? BlockState.Recoverable : BlockState.Unrecoverable);
            }
        }

        observer.Log(result.AllFilesComplete ? ParLogLevel.Info : ParLogLevel.Warning,
            result.AllFilesComplete
                ? "All files are correct."
                : $"{result.MissingSlices} slice(s) missing, {result.RecoverySlicesAvailable} recovery slice(s) available: repair {(possible ? "is possible" : "is NOT possible")}.");
        return result;
    }

    /// <summary>Reads the file sequentially, checking every slice at its expected offset. Returns true if complete.</summary>
    private static bool CheckInPlace(Par2FileVerification fv, Par2RecoverySet set, Par2SliceMatcher matcher, SliceLocation?[] sources, FileHandleCache handles, BlockStateMap map)
    {
        var f = fv.File;
        long len = new FileInfo(fv.TargetPath).Length;
        using var fileMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buf = new byte[set.SliceSize];
        bool allSlices = f.SliceChecksums != null;
        long sliceCountInFile = (len + set.SliceSize - 1) / set.SliceSize;
        for (long s = 0; s < sliceCountInFile; s++)
        {
            int n = handles.ReadPadded(fv.TargetPath, s * set.SliceSize, buf);
            fileMd5.AppendData(buf, 0, n);
            if (s >= f.SliceCount || f.SliceChecksums == null)
            {
                allSlices = false;
                continue;
            }

            int idx = f.FirstSlice + (int)s;
            var expected = f.SliceChecksums[(int)s];
            if (Crc32Ieee.Compute(buf) == expected.Crc32 && Md5Hash.Compute(buf) == expected.Md5)
            {
                sources[idx] = new SliceLocation(fv.TargetPath, s * set.SliceSize, n);
                map.Set(idx, BlockState.Found);
            }
            else
            {
                allSlices = false;
                map.Set(idx, BlockState.Damaged);
            }
        }

        var hash = Md5Hash.FromBytes(fileMd5.GetHashAndReset());
        return len == f.Length && hash == f.FileHash && (allSlices || f.SliceCount == 0);
    }

    private static void Record(FoundBlock hit, string path, Par2RecoverySet set, Par2SliceMatcher matcher, SliceLocation?[] sources, BlockStateMap map, List<Par2FileVerification> files)
    {
        // The matcher reports a representative; every slice with identical contents is satisfied by this data.
        foreach (int idx in matcher.SlicesWithSameContent(hit.BlockIndex))
        {
            if (sources[idx] != null)
            {
                continue;
            }

            sources[idx] = new SliceLocation(path, hit.Offset, hit.Length);
            var owner = files.First(f => idx >= f.File.FirstSlice && idx < f.File.FirstSlice + f.File.SliceCount);
            long expectedOffset = (idx - owner.File.FirstSlice) * set.SliceSize;
            var state = string.Equals(owner.TargetPath, path, StringComparison.Ordinal)
                ? (hit.Offset == expectedOffset ? BlockState.Found : BlockState.FoundMoved)
                : BlockState.FoundElsewhere;
            map.Set(idx, state);
            if (state != BlockState.Found)
            {
                map.FoundAt[idx] = new BlockLocation(path, hit.Offset, hit.Length);
            }
        }
    }

    private static Md5Hash Md5OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Md5Hash.FromBytes(MD5.HashData(fs));
    }
}

/// <summary>
/// PAR2 slice matching for the <see cref="BlockScanner"/>: CRC-32 rolling checksum + MD5 confirmation.
/// Slices with identical contents are grouped so one found copy satisfies all of them.
/// </summary>
internal sealed class Par2SliceMatcher : IBlockMatcher
{
    private readonly Dictionary<uint, List<int>> _byCrc = new();
    private readonly (Md5Hash Md5, uint Crc)[] _checks;
    private readonly bool[] _isTail;
    private readonly Dictionary<(Md5Hash, uint), List<int>> _groups = new();
    private readonly Dictionary<int, List<int>> _tailsByLength = new();
    private readonly ulong[] _filter = new ulong[1 << 14]; // 2^20 bit membership filter on the CRC
    private readonly RollingCrc32 _roll;
    private readonly long _sliceSize;

    public Par2SliceMatcher(Par2RecoverySet set)
    {
        _sliceSize = set.SliceSize;
        BlockSize = (int)set.SliceSize;
        _roll = new RollingCrc32(BlockSize);
        _checks = new (Md5Hash, uint)[set.TotalSlices];
        _isTail = new bool[set.TotalSlices];
        foreach (var f in set.Files)
        {
            if (f.SliceChecksums == null)
            {
                continue;
            }

            for (int s = 0; s < f.SliceCount && s < f.SliceChecksums.Count; s++)
            {
                int idx = f.FirstSlice + s;
                var c = f.SliceChecksums[s];
                _checks[idx] = c;
                if (!_groups.TryGetValue(c, out var g))
                {
                    _groups[c] = g = new List<int>();
                    if (!_byCrc.TryGetValue(c.Crc32, out var l))
                    {
                        _byCrc[c.Crc32] = l = new List<int>();
                    }

                    l.Add(idx);
                    _filter[(c.Crc32 & 0xFFFFF) >> 6] |= 1UL << (int)(c.Crc32 & 63);
                }

                g.Add(idx);
                bool lastSlice = s == f.SliceCount - 1;
                long tailLen = f.Length - s * set.SliceSize;
                if (lastSlice && tailLen > 0 && tailLen < set.SliceSize)
                {
                    _isTail[idx] = true;
                    if (!_tailsByLength.TryGetValue((int)tailLen, out var t))
                    {
                        _tailsByLength[(int)tailLen] = t = new List<int>();
                    }

                    t.Add(idx);
                }
            }
        }
    }

    public int BlockSize { get; }

    public IReadOnlyCollection<int> TailLengths => _tailsByLength.Keys;

    public ulong Weak(ReadOnlySpan<byte> window) => Crc32Ieee.Compute(window);

    public ulong Slide(ulong weak, byte outgoing, byte incoming) => _roll.Slide((uint)weak, outgoing, incoming);

    public bool MayMatch(ulong weak) => (_filter[(weak & 0xFFFFF) >> 6] & (1UL << (int)(weak & 63))) != 0;

    public int MatchFull(ulong weak, ReadOnlySpan<byte> window)
    {
        if (!_byCrc.TryGetValue((uint)weak, out var candidates))
        {
            return -1;
        }

        var md5 = Md5Hash.Compute(window);
        foreach (var idx in candidates)
        {
            if (_checks[idx].Md5 == md5)
            {
                return idx;
            }
        }

        return -1;
    }

    public int MatchTail(ReadOnlySpan<byte> data)
    {
        if (!_tailsByLength.TryGetValue(data.Length, out var tails))
        {
            return -1;
        }

        var padded = new byte[_sliceSize];
        data.CopyTo(padded);
        uint crc = Crc32Ieee.Compute(padded);
        Md5Hash? md5 = null;
        foreach (var idx in tails)
        {
            if (_checks[idx].Crc != crc)
            {
                continue;
            }

            md5 ??= Md5Hash.Compute(padded);
            if (_checks[idx].Md5 == md5)
            {
                return idx;
            }
        }

        return -1;
    }

    /// <summary>All slices whose contents equal those of <paramref name="index"/> (including itself).</summary>
    public IReadOnlyList<int> SlicesWithSameContent(int index) => _groups[_checks[index]];
}

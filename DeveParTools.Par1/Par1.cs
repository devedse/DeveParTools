using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Core.Util;

namespace DeveParTools.Par1;

/// <summary>One file entry of a PAR1 file list.</summary>
public sealed record Par1FileEntry(ulong Status, long Size, byte[] Md5, byte[] Md5Of16k, string Name)
{
    /// <summary>Bit 0: the file is included in the parity data.</summary>
    public bool IsSaved => (Status & 1) != 0;
}

/// <summary>A parsed .par or .pXX file.</summary>
public sealed class Par1File
{
    public const int HeaderSize = 0x60;
    public static readonly byte[] Magic = "PAR\0\0\0\0\0"u8.ToArray();
    public const uint FileVersion = 0x00010000;

    /// <summary>Program identifier written into the high dword of the version (02 = PAR).</summary>
    public const uint ProgramVersion = 0x02000000;

    public required string Path { get; init; }

    public required uint ProgramId { get; init; }

    public required byte[] SetHash { get; init; }

    /// <summary>0 for the .par index file, n for .pNN recovery volumes.</summary>
    public required ulong VolumeNumber { get; init; }

    public required IReadOnlyList<Par1FileEntry> Files { get; init; }

    public required long DataOffset { get; init; }

    public required long DataSize { get; init; }

    /// <summary>Whether the control hash (MD5 of bytes 0x20..end) matched.</summary>
    public required bool ControlHashValid { get; init; }

    public string? Comment { get; init; }

    /// <summary>Reads a PAR1 file; returns null when it is not a PAR1 file.</summary>
    public static Par1File? Read(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < HeaderSize)
        {
            return null;
        }

        var h = new byte[HeaderSize];
        fs.ReadExactly(h);
        if (!h.AsSpan(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)) != FileVersion)
        {
            return null;
        }

        fs.Position = 0x20;
        var control = MD5.HashData(fs);
        bool controlOk = control.AsSpan().SequenceEqual(h.AsSpan(0x10, 16));

        // The header fields are unsigned 64-bit values. Read as long, a damaged value of 2^63 or more turns negative,
        // and "offset + size" could overflow, so every range is checked as "size <= length - offset".
        long count = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x38));
        long listOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x40));
        long listSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x48));
        long dataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x50));
        long dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x58));
        if (listOffset < HeaderSize || listSize < 0 || listSize > fs.Length - listOffset || count < 0 || count > 1 << 16)
        {
            return null;
        }

        var list = new byte[listSize];
        fs.Position = listOffset;
        fs.ReadExactly(list);
        var files = new List<Par1FileEntry>();
        int p = 0;
        for (long i = 0; i < count; i++)
        {
            if (p + 0x38 > list.Length)
            {
                return null;
            }

            long entrySize = (long)BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(p));
            if (entrySize < 0x38 || p + entrySize > list.Length)
            {
                return null;
            }

            var name = Encoding.Unicode.GetString(list.AsSpan(p + 0x38, (int)((entrySize - 0x38) & ~1)));
            files.Add(new Par1FileEntry(BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(p + 8)), (long)BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(p + 0x10)),
                list.AsSpan(p + 0x18, 16).ToArray(), list.AsSpan(p + 0x28, 16).ToArray(), name.TrimEnd('\0')));
            p += (int)entrySize;
        }

        ulong volume = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x30));
        string? comment = null;
        if (volume == 0 && dataSize > 0 && dataOffset >= 0 && dataSize <= fs.Length - dataOffset)
        {
            var c = new byte[dataSize];
            fs.Position = dataOffset;
            fs.ReadExactly(c);
            comment = Encoding.Unicode.GetString(c);
        }

        return new Par1File
        {
            Path = path,
            ProgramId = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)),
            SetHash = h.AsSpan(0x20, 16).ToArray(),
            VolumeNumber = volume,
            Files = files,
            DataOffset = dataOffset,
            DataSize = dataSize,
            ControlHashValid = controlOk,
            Comment = comment,
        };
    }

    /// <summary>Writes a PAR1 file with the given header fields, file list and data (computes both hashes).</summary>
    /// <summary>
    /// The size of a file written by <see cref="Write"/>: the 0x60-byte header, a 0x38-byte entry plus the UTF-16 name
    /// for every file, then the data.
    /// </summary>
    public static long SizeOf(IEnumerable<string> names, long dataSize) => HeaderSize + names.Sum(n => 0x38L + Encoding.Unicode.GetByteCount(n)) + dataSize;

    public static void Write(string path, ulong volumeNumber, IReadOnlyList<Par1FileEntry> files, Action<Stream> writeData, long dataSize)
    {
        var list = new MemoryStream();
        Span<byte> b8 = stackalloc byte[8];
        foreach (var f in files)
        {
            var name = Encoding.Unicode.GetBytes(f.Name);
            BinaryPrimitives.WriteUInt64LittleEndian(b8, (ulong)(0x38 + name.Length));
            list.Write(b8);
            BinaryPrimitives.WriteUInt64LittleEndian(b8, f.Status);
            list.Write(b8);
            BinaryPrimitives.WriteUInt64LittleEndian(b8, (ulong)f.Size);
            list.Write(b8);
            list.Write(f.Md5);
            list.Write(f.Md5Of16k);
            list.Write(name);
        }

        var h = new byte[HeaderSize];
        Magic.CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), FileVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), ProgramVersion);
        SetHashOf(files).CopyTo(h, 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x30), volumeNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x38), (ulong)files.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x40), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x48), (ulong)list.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x50), (ulong)(HeaderSize + list.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x58), (ulong)dataSize);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            fs.Write(h);
            list.Position = 0;
            list.CopyTo(fs);
            writeData(fs);
            fs.Position = 0x20;
            var control = MD5.HashData(fs);
            fs.Position = 0x10;
            fs.Write(control);
        }
    }

    /// <summary>Set hash: MD5 of the concatenated MD5s of the saved files, in list order.</summary>
    public static byte[] SetHashOf(IReadOnlyList<Par1FileEntry> files)
    {
        var all = files.Where(f => f.IsSaved).SelectMany(f => f.Md5).ToArray();
        return MD5.HashData(all);
    }

    /// <summary>Volume n's extension: .p01 .. .p99, then .q00, .q01, ...</summary>
    public static string VolumeExtension(int volume) => volume == 0 ? ".par" : $".{(char)('p' + volume / 100)}{volume % 100:D2}";
}

/// <summary>
/// The PAR1 code: GF(2^8) (generator 0x11D) Reed-Solomon after Plank's 1996 tutorial. File j (1-based among the saved
/// files) has base j; volume v holds, byte position by byte position, <c>C_v = Σ_j j^(v−1)·D_j</c>, where shorter
/// files are padded with zeros up to the largest file. Example: volume 1 is the plain XOR of all files
/// (every coefficient is j^0 = 1); volume 2 is <c>1·D1 ^ 2·D2 ^ 3·D3 ...</c>.
/// </summary>
/// <remarks>
/// <para><b>Known flaw of the format.</b> Repairing L missing files with L volumes inverts the L×L matrix
/// <c>A[r][c] = j_c^(v_r − 1)</c>. For consecutive volumes this is a Vandermonde matrix and always invertible, but
/// for volumes with gaps it can be singular (the flaw Plank corrected in 2003). The smallest example: files 7 and 24
/// missing, only volumes 1 and 4 left. Then</para>
/// <code>det A = 7^0·24^3 − 7^3·24^0 = 24^3 + 7^3 = 0     because (24/7)^3 = 1 in GF(2^8)</code>
/// <para>(24/7 = 214 = 2^85 has order 3), so those two files cannot be rebuilt from those two volumes, although
/// the counts match. <see cref="Par1Set.Repair"/> then tries the next volumes, and fails instead of writing wrong
/// data when no selection works.</para>
/// </remarks>
public static class Par1Math
{
    public static GaloisField8 Field => GaloisField8.Default;

    public static byte Coefficient(int fileIndex, int volume) => Field.Pow((byte)(fileIndex + 1), volume - 1);
}

public sealed class Par1CreateOptions
{
    public required IReadOnlyList<string> InputFiles { get; init; }

    /// <summary>Path of the .par index file; volumes get .p01, .p02, ... extensions.</summary>
    public required string OutputPath { get; init; }

    public int RecoveryVolumes { get; init; } = 1;

    public string? Comment { get; init; }
}

public enum Par1FileStatus
{
    Complete,
    Damaged,
    Missing,
}

public sealed class Par1FileVerification
{
    public required Par1FileEntry Entry { get; init; }

    public required string TargetPath { get; init; }

    public Par1FileStatus Status { get; set; }

    /// <summary>A complete copy found under another name.</summary>
    public string? FoundAs { get; set; }
}

public sealed class Par1VerifyResult
{
    public required Par1File Index { get; init; }

    public required IReadOnlyList<Par1File> Volumes { get; init; }

    public required IReadOnlyList<Par1FileVerification> Files { get; init; }

    public int MissingSavedFiles => Files.Count(f => f.Entry.IsSaved && f.Status != Par1FileStatus.Complete && f.FoundAs == null);

    public bool AllFilesComplete => Files.All(f => f.Status == Par1FileStatus.Complete);

    public bool RepairPossible => MissingSavedFiles <= Volumes.Count && Files.Where(f => !f.Entry.IsSaved).All(f => f.Status == Par1FileStatus.Complete || f.FoundAs != null);
}

/// <summary>Create, verify and repair PAR 1.0 sets (whole files are the recovery unit).</summary>
public static class Par1Set
{
    /// <summary>
    /// The files <see cref="Create"/> writes and their exact sizes, without reading the inputs: the index file holds the
    /// file list and the comment (UTF-16), every volume the file list and one block the size of the largest file.
    /// </summary>
    public static IReadOnlyList<OutputFileSize> PlanFileSizes(Par1CreateOptions options)
    {
        var names = options.InputFiles.Select(Path.GetFileName).Select(n => n!).ToList();
        long largest = options.InputFiles.Select(f => new FileInfo(f).Length).DefaultIfEmpty(0).Max();
        var baseName = options.OutputPath.EndsWith(".par", StringComparison.OrdinalIgnoreCase) ? options.OutputPath[..^4] : options.OutputPath;
        var sizes = new List<OutputFileSize> { new(baseName + ".par", Par1File.SizeOf(names, Encoding.Unicode.GetByteCount(options.Comment ?? string.Empty)), 0) };
        for (int v = 1; v <= options.RecoveryVolumes; v++)
        {
            sizes.Add(new OutputFileSize(baseName + Par1File.VolumeExtension(v), Par1File.SizeOf(names, largest), 1));
        }

        return sizes;
    }

    /// <summary>Creates the .par index file and <see cref="Par1CreateOptions.RecoveryVolumes"/> .pXX volumes.</summary>
    /// <remarks>
    /// PAR1 stores bare file names (no directories) and <see cref="Verify"/> looks for the files next to the .par file,
    /// so every input must be in the directory of <see cref="Par1CreateOptions.OutputPath"/> and no two inputs may
    /// have the same name. For example, with the output "/data/set.par":
    /// <code>
    /// /data/a.bin, /data/b.bin        ok
    /// /data/a.bin, /data/sub/c.bin    ArgumentException: "/data/sub/c.bin" is not in "/data"
    /// /data/a.bin, /other/a.bin       ArgumentException: the name "a.bin" occurs twice
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentException">No inputs, too many files plus volumes, duplicate names, or an input outside
    /// the directory of the .par file.</exception>
    public static IReadOnlyList<string> Create(Par1CreateOptions options, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var files = options.InputFiles.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        if (files.Count == 0 || files.Count + options.RecoveryVolumes > 255 || options.RecoveryVolumes < 0)
        {
            throw new ArgumentException("PAR1 needs 1..255 files plus volumes in total.");
        }

        var duplicate = files.GroupBy(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new ArgumentException($"PAR1 stores bare file names, but the name '{duplicate.Key}' occurs more than once: {string.Join(", ", duplicate)}.", nameof(options));
        }

        var outputDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!;
        foreach (var f in files)
        {
            // GetRelativePath compares with the platform's case rules; it gives just the name only for a file
            // directly inside outputDir.
            if (Path.GetRelativePath(outputDir, f) != Path.GetFileName(f))
            {
                throw new ArgumentException($"PAR1 stores bare file names, so every input must be in the directory of the .par file ('{outputDir}'), but '{f}' is not.", nameof(options));
            }
        }

        var observer = context.Observer;
        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        long largest = files.Max(f => new FileInfo(f).Length);
        int volumeCount = options.RecoveryVolumes;
        var stages = new List<OperationStage>
        {
            new("Hashing", totalBytes, "Reads every file once and computes its MD5 checksum (and that of its first 16 KiB), which identify the file and show whether it is intact."),
            new("Writing the index file", files.Count * 600.0, "Writes the .par file: the list of files with their sizes and checksums."),
        };
        if (volumeCount > 0)
        {
            stages.Add(new("Encoding", totalBytes + (double)volumeCount * largest, $"Reads every file again and computes the {volumeCount:N0} recovery volume(s) (Reed-Solomon over GF(2^8)); each is as large as the largest file."));
            stages.Add(new("Writing recovery files", 2.0 * volumeCount * largest, "Copies each computed volume into its .pXX file after the file list, and computes the file's checksum."));
        }

        observer.Plan(stages);
        observer.Stage("Hashing");
        long hashed = 0;
        var entries = files.Select(f =>
        {
            observer.Progress(totalBytes == 0 ? 1 : (double)hashed / totalBytes, Path.GetFileName(f));
            hashed += new FileInfo(f).Length;
            using var fs = File.OpenRead(f);
            var full = MD5.HashData(fs);
            fs.Position = 0;
            var buf = new byte[16384];
            int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return new Par1FileEntry(1, fs.Length, full, MD5.HashData(buf.AsSpan(0, n)), Path.GetFileName(f));
        }).ToList();

        var written = new List<string>();
        var baseName = options.OutputPath.EndsWith(".par", StringComparison.OrdinalIgnoreCase) ? options.OutputPath[..^4] : options.OutputPath;
        var comment = Encoding.Unicode.GetBytes(options.Comment ?? string.Empty);
        observer.Stage("Writing the index file");
        Par1File.Write(baseName + ".par", 0, entries, s => s.Write(comment), comment.Length);
        written.Add(baseName + ".par");

        long size = entries.Max(e => e.Size);
        int volumes = options.RecoveryVolumes;
        if (volumes > 0)
        {
            context.Observer.Stage("Encoding");
            var outputs = Enumerable.Range(1, volumes).Select(v => baseName + Par1File.VolumeExtension(v)).ToList();
            var streams = outputs.Select(o => new FileStream(o + ".tmp", FileMode.Create, FileAccess.ReadWrite)).ToList();
            try
            {
                foreach (var s in streams)
                {
                    s.SetLength(size);
                }

                using var handles = new FileHandleCache();
                var source = new FileRegions(files, handles);
                LinearCombiner.Combine(new Gf8Field(Par1Math.Field), new Coefficients(Enumerable.Range(1, volumes).ToArray()), files.Count, volumes, source,
                    new StreamSink(streams), size, context.Combine.Create(observer.ToProgress()));
            }
            finally
            {
                foreach (var s in streams)
                {
                    s.Dispose();
                }
            }

            observer.Stage("Writing recovery files");
            for (int v = 0; v < volumes; v++)
            {
                observer.Progress((double)v / volumes);
                var tmp = outputs[v] + ".tmp";
                Par1File.Write(outputs[v], (ulong)(v + 1), entries, s =>
                {
                    using var data = File.OpenRead(tmp);
                    data.CopyTo(s);
                }, size);
                File.Delete(tmp);
                written.Add(outputs[v]);
            }
        }

        return written;
    }

    /// <summary>Verifies a PAR1 set. Volumes are found next to the index file by extension and set hash.</summary>
    public static Par1VerifyResult Verify(string parPath, IReadOnlyList<string>? extraFiles = null, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var observer = context.Observer;
        var full = Path.GetFullPath(parPath);
        var dir = Path.GetDirectoryName(full)!;
        var baseName = Path.GetFileNameWithoutExtension(full);
        var volumeFiles = Directory.EnumerateFiles(dir, baseName + ".*").Where(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            return ext.Length == 4 && ext[1] >= 'p' && ext[1] <= 'z' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]);
        }).ToList();
        long parBytes = new FileInfo(full).Length + volumeFiles.Sum(f => new FileInfo(f).Length);
        observer.Plan(VerifyStages.Plan(parBytes, parBytes, 0));
        observer.Stage(VerifyStages.Loading);
        var index = Par1File.Read(full) ?? throw new InvalidDataException($"'{parPath}' is not a PAR1 file.");
        var volumes = new List<Par1File>();
        long loaded = new FileInfo(full).Length;
        foreach (var f in volumeFiles)
        {
            observer.Progress((double)loaded / parBytes, Path.GetFileName(f));
            loaded += new FileInfo(f).Length;
            var v = Par1File.Read(f);
            if (v != null && v.VolumeNumber > 0 && v.ControlHashValid && v.SetHash.AsSpan().SequenceEqual(index.SetHash) && volumes.All(o => o.VolumeNumber != v.VolumeNumber))
            {
                volumes.Add(v);
            }
        }

        // Directory order is arbitrary; volume order makes the result (and the block map) deterministic.
        volumes.Sort((a, b) => a.VolumeNumber.CompareTo(b.VolumeNumber));
        var map = context.CreateBlockMap(index.Files.Count, volumes.Count);
        for (int i = 0; i < volumes.Count; i++)
        {
            map.SetRecovery(i, RecoveryBlockState.Available);
            map.RecoveryContainer[i] = Path.GetFileName(volumes[i].Path);
        }

        var candidates = Directory.EnumerateFiles(dir).Concat((extraFiles ?? []).Select(Path.GetFullPath)).Distinct(StringComparer.Ordinal).ToList();
        var results = new List<Par1FileVerification>();
        long dataBytes = Math.Max(1, index.Files.Sum(e => e.Size));
        long verified = 0;
        observer.Plan(VerifyStages.Plan(parBytes, dataBytes, 0));
        observer.Stage(VerifyStages.Verifying);
        for (int i = 0; i < index.Files.Count; i++)
        {
            var e = index.Files[i];
            observer.Progress((double)verified / dataBytes, e.Name);
            verified += e.Size;
            var target = TargetPathOf(dir, e.Name);
            var fv = new Par1FileVerification { Entry = e, TargetPath = target };
            map.Ownership[i] = [new BlockLocation(target, 0, (int)Math.Min(int.MaxValue, e.Size))];
            if (!File.Exists(target))
            {
                fv.Status = Par1FileStatus.Missing;
            }
            else
            {
                fv.Status = new FileInfo(target).Length == e.Size && Md5Of(target).AsSpan().SequenceEqual(e.Md5) ? Par1FileStatus.Complete : Par1FileStatus.Damaged;
            }

            // A file without a usable name can never be restored, so a copy of it elsewhere does not help.
            if (fv.Status != Par1FileStatus.Complete && target.Length > 0)
            {
                // Renamed copy? Compare size and the 16 KiB hash first (cheap), then the full MD5.
                foreach (var c in candidates)
                {
                    if (c != target && File.Exists(c) && new FileInfo(c).Length == e.Size && Md5Of16k(c).AsSpan().SequenceEqual(e.Md5Of16k) && Md5Of(c).AsSpan().SequenceEqual(e.Md5))
                    {
                        fv.FoundAs = c;
                        break;
                    }
                }
            }

            map.Set(i, fv.Status == Par1FileStatus.Complete ? BlockState.Found : fv.FoundAs != null ? BlockState.FoundElsewhere : fv.Status == Par1FileStatus.Damaged ? BlockState.Damaged : BlockState.Missing);
            results.Add(fv);
        }

        var result = new Par1VerifyResult { Index = index, Volumes = volumes, Files = results };
        for (int i = 0; i < results.Count; i++)
        {
            if (map.Get(i) is BlockState.Damaged or BlockState.Missing)
            {
                map.Set(i, result.RepairPossible ? BlockState.Recoverable : BlockState.Unrecoverable);
            }
        }

        return result;
    }

    /// <summary>
    /// Where the file of a stored name belongs: in <paramref name="dir"/>, under the part of the name after the last
    /// '/' or '\' (PAR1 has no directories). Names that are not a plain file name ("", ".", "..", "C:x", ...) give an
    /// empty path, so a crafted .par file cannot make Repair write outside the directory.
    /// </summary>
    /// <example><c>"a.bin"</c> → "dir/a.bin"; <c>"sub\a.bin"</c> → "dir/a.bin"; <c>".."</c> → "".</example>
    internal static string TargetPathOf(string dir, string storedName)
    {
        var name = storedName[(storedName.LastIndexOfAny(['/', '\\']) + 1)..];
        return PathSafety.TryResolveUnder(dir, name, out var path, out _) ? path : string.Empty;
    }

    /// <summary>
    /// Restores renamed copies, then rebuilds the missing saved files from the recovery volumes. Returns true when
    /// every file is complete afterwards.
    /// </summary>
    public static bool Repair(Par1VerifyResult verify, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        if (verify.AllFilesComplete)
        {
            return true;
        }

        if (!verify.RepairPossible)
        {
            return false;
        }

        // Renamed copies first.
        var observer = context.Observer;
        var savedPlan = verify.Files.Where(f => f.Entry.IsSaved).ToList();
        int missingPlan = savedPlan.Count(f => f.Status != Par1FileStatus.Complete && f.FoundAs == null);
        long largestPlan = savedPlan.Select(f => f.Entry.Size).DefaultIfEmpty(0).Max();
        var stages = new List<OperationStage> { new(RepairStages.Copying, verify.Files.Where(f => f.FoundAs != null).Sum(f => f.Entry.Size) + 1.0, "Copies complete files that were found under another name into place.") };
        if (missingPlan > 0)
        {
            stages.Add(new(RepairStages.Reconstructing, (double)(savedPlan.Count + missingPlan) * largestPlan, RepairStages.ReconstructingDescription(missingPlan) + " (PAR1: every file is one block.)"));
            stages.Add(new(RepairStages.Writing, (double)missingPlan * largestPlan, "Checks the MD5 of every rebuilt file and writes it in place."));
        }

        observer.Plan(stages);
        observer.Stage(RepairStages.Copying);
        foreach (var f in verify.Files.Where(f => f.Status != Par1FileStatus.Complete && f.FoundAs != null))
        {
            File.Copy(f.FoundAs!, f.TargetPath + ".tmp", true);
            File.Move(f.TargetPath + ".tmp", f.TargetPath, true);
            f.Status = Par1FileStatus.Complete;
        }

        var saved = verify.Files.Where(f => f.Entry.IsSaved).ToList();
        var missing = Enumerable.Range(0, saved.Count).Where(i => saved[i].Status != Par1FileStatus.Complete).ToList();
        if (missing.Count == 0)
        {
            return true;
        }

        var field = new Gf8Field(Par1Math.Field);
        int l = missing.Count;
        if (ChooseVolumes(verify.Volumes, missing, context.CancellationToken) is not var (chosen, a))
        {
            return false;
        }

        long size = saved.Max(f => f.Entry.Size);
        var known = Enumerable.Range(0, saved.Count).Where(i => !missing.Contains(i)).ToList();
        using var handles = new FileHandleCache();

        // S_r = R_r + Σ_known c(i, v_r)·D_i, then D_missing = A⁻¹·S.
        var inputs = known.Select(i => saved[i].TargetPath).Concat(chosen.Select(v => v.Path)).ToList();
        var offsets = known.Select(_ => 0L).Concat(chosen.Select(v => v.DataOffset)).ToList();
        var lengths = known.Select(i => saved[i].Entry.Size).Concat(chosen.Select(v => v.DataSize)).ToList();
        var source = new FileRegions(inputs, handles, offsets, lengths);
        var s = Enumerable.Range(0, l).Select(_ => new byte[size]).ToArray();
        var coeffs = new GfMatrix<byte>(l, inputs.Count, field);
        for (int r = 0; r < l; r++)
        {
            for (int k = 0; k < known.Count; k++)
            {
                coeffs[r, k] = Par1Math.Coefficient(known[k], (int)chosen[r].VolumeNumber);
            }

            coeffs[r, known.Count + r] = 1;
        }

        // Most of the work is reading every input (first part); the second combination is small and in memory.
        observer.Stage(RepairStages.Reconstructing);
        LinearCombiner.Combine(field, new MatrixCoefficients<byte>(coeffs), inputs.Count, l, source, new MemoryRegions(s), size, context.Combine.Create(observer.ToProgress(0, 0.9)));
        var d = Enumerable.Range(0, l).Select(_ => new byte[size]).ToArray();
        LinearCombiner.Combine(field, new MatrixCoefficients<byte>(a), l, l, new MemoryRegions(s), new MemoryRegions(d), size, context.Combine.Create(observer.ToProgress(0.9, 1)));

        observer.Stage(RepairStages.Writing);

        bool ok = true;
        for (int j = 0; j < l; j++)
        {
            observer.Progress((double)j / l);
            var f = saved[missing[j]];
            var data = d[j].AsSpan(0, (int)f.Entry.Size);
            if (f.TargetPath.Length == 0 || !MD5.HashData(data).AsSpan().SequenceEqual(f.Entry.Md5))
            {
                ok = false;
                continue;
            }

            handles.Close(f.TargetPath);
            File.WriteAllBytes(f.TargetPath + ".tmp", data.ToArray());
            File.Move(f.TargetPath + ".tmp", f.TargetPath, true);
            f.Status = Par1FileStatus.Complete;
        }

        return ok;
    }

    /// <summary>
    /// L = |missing| volumes whose matrix <c>A[r][c] = (missing[c] + 1)^(v_r − 1)</c> is invertible: the first L by
    /// volume number, else the window slides along the sorted volumes; null when no window works (see the remarks of
    /// <see cref="Par1Math"/>). Example: files 7 and 24 missing with volumes 1, 4 and 5 left: {1, 4} is singular,
    /// {4, 5} is used.
    /// </summary>
    internal static (List<Par1File> Volumes, GfMatrix<byte> Inverse)? ChooseVolumes(IReadOnlyList<Par1File> volumes, List<int> missing, CancellationToken cancellationToken)
    {
        var field = new Gf8Field(Par1Math.Field);
        int l = missing.Count;
        var sorted = volumes.OrderBy(v => v.VolumeNumber).ToList();
        for (int start = 0; start + l <= sorted.Count; start++)
        {
            var candidate = sorted.GetRange(start, l);
            var a = new GfMatrix<byte>(l, l, field);
            for (int r = 0; r < l; r++)
            {
                for (int c = 0; c < l; c++)
                {
                    a[r, c] = Par1Math.Coefficient(missing[c], (int)candidate[r].VolumeNumber);
                }
            }

            if (a.TryInvert(cancellationToken))
            {
                return (candidate, a);
            }
        }

        return null;
    }

    private static byte[] Md5Of(string path)
    {
        using var fs = File.OpenRead(path);
        return MD5.HashData(fs);
    }

    private static byte[] Md5Of16k(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[16384];
        int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        return MD5.HashData(buf.AsSpan(0, n));
    }

    private sealed class Coefficients(int[] volumes) : ICoefficientProvider<byte>
    {
        public void Fill(int output, int firstInput, Span<byte> destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = Par1Math.Coefficient(firstInput + i, volumes[output]);
            }
        }
    }

    /// <summary>Whole files (or file ranges) as zero-padded regions.</summary>
    private sealed class FileRegions(IReadOnlyList<string> paths, FileHandleCache handles, IReadOnlyList<long>? offsets = null, IReadOnlyList<long>? lengths = null) : IRegionSource
    {
        public void Read(int index, long offset, Span<byte> destination)
        {
            destination.Clear();
            long baseOffset = offsets?[index] ?? 0;
            long length = lengths?[index] ?? new FileInfo(paths[index]).Length;
            long available = Math.Clamp(length - offset, 0, destination.Length);
            if (available > 0)
            {
                handles.ReadPadded(paths[index], baseOffset + offset, destination[..(int)available]);
            }
        }
    }

    private sealed class StreamSink(List<FileStream> streams) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source) => RandomAccess.Write(streams[index].SafeFileHandle, source, offset);
    }
}

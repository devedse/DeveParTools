using System.Globalization;
using System.Security.Cryptography;
using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Core.Util;
using DeveParTools.Par2.Packets;

namespace DeveParTools.Par2;

/// <summary>The steps of creating (and extending) a PAR2 set, with their names and what they do.</summary>
public static class Par2Stages
{
    public const string Preparing = "Preparing";
    public const string Hashing = "Hashing";
    public const string Encoding = "Encoding";
    public const string WritingRecovery = "Writing recovery files";
    public const string WritingIndex = "Writing the index file";

    /// <summary>
    /// The steps of <see cref="Par2Creator.Create"/>, weighted by the bytes each reads or writes. With enough memory
    /// for whole slices the input is read once while encoding (the checksums are computed on the way); otherwise it is
    /// hashed first and then encoded in <paramref name="passes"/> passes, each reading the whole input.
    /// </summary>
    internal static IReadOnlyList<OperationStage> ForCreate(int files, long inputBytes, int slices, int recovery, long sliceSize, int passes, int recoveryFiles)
    {
        double critical = files * 200.0 + slices * 20.0;
        var stages = new List<OperationStage>
        {
            new(Preparing, files * 16384.0 + 4096, "Lists the files and reads the first 16 KiB of each one, which (with its name and size) identifies the file in the set."),
        };
        if (passes > 1 || recovery == 0)
        {
            stages.Add(new(Hashing, inputBytes, "Reads every file once and computes the checksums of every block (MD5 and CRC-32) and of every whole file."));
        }

        if (recovery > 0)
        {
            stages.Add(new(Encoding, inputBytes * passes + (double)recovery * sliceSize, passes == 1
                ? $"Reads every file once and computes the {recovery:N0} recovery blocks (PAR2 Reed-Solomon over GF(2^16)), and the checksums on the way. The recovery files were created at their final size when this step started; they are filled in now."
                : $"Computes the {recovery:N0} recovery blocks (PAR2 Reed-Solomon over GF(2^16)). The memory budget holds part of every block, so the files are read {passes} times, one part each time. The recovery files were created at their final size when this step started; they are filled in now."));
            stages.Add(new(WritingRecovery, (passes > 1 ? (double)recovery * sliceSize : 0) + critical * (recoveryFiles + Math.Log2(Math.Max(2, recovery))) + recovery * 68.0,
                "Completes the checksum of every recovery block and writes copies of the set's description (file list and block checksums) into every recovery file, so each one can also be used on its own."));
        }

        stages.Add(new(WritingIndex, critical, "Writes the index file: the description of the set, once."));
        return stages;
    }
}

public sealed class Par2CreateOptions
{
    public required IReadOnlyList<string> InputFiles { get; init; }

    /// <summary>Path of the index file, e.g. "/data/set.par2". Volume files get "set.volXX+YY.par2" names.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Directory that stored names are relative to (default: directory of <see cref="OutputPath"/>).</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>Explicit slice size in bytes (multiple of 4). When null, it is derived from <see cref="SliceCount"/>.</summary>
    public long? SliceSize { get; init; }

    /// <summary>Target number of input slices when <see cref="SliceSize"/> is not set.</summary>
    public int SliceCount { get; init; } = 2000;

    /// <summary>Exact number of recovery slices. When null, <see cref="RedundancyPercent"/> is used.</summary>
    public int? RecoverySliceCount { get; init; }

    public double RedundancyPercent { get; init; } = 10;

    public int FirstExponent { get; init; }

    /// <summary>How the recovery slices are spread over the volume files.</summary>
    public RecoveryFileScheme Scheme { get; init; } = RecoveryFileScheme.Exponential;

    /// <summary>
    /// Number of volume files for <see cref="RecoveryFileScheme.Uniform"/> (default 1) and
    /// <see cref="RecoveryFileScheme.Exponential"/> (default: as many as the doubling needs). At most one file per
    /// recovery slice.
    /// </summary>
    public int? VolumeCount { get; init; }

    public string? Comment { get; init; }

    public string Creator { get; init; } = Par2Creator.DefaultCreator;

    /// <summary>
    /// False: write only the recovery volumes, and refuse to overwrite existing ones. Used to add volumes to an existing
    /// set (see <see cref="Par2Extender"/>), whose index file stays as it is.
    /// </summary>
    public bool WriteIndexFile { get; init; } = true;
}

public sealed class Par2CreateResult
{
    public required Md5Hash SetId { get; init; }

    public required long SliceSize { get; init; }

    public required int InputSlices { get; init; }

    public required int RecoverySlices { get; init; }

    public required IReadOnlyList<string> WrittenFiles { get; init; }
}

/// <summary>Creates PAR 2.0 recovery sets.</summary>
public static class Par2Creator
{
    public const string DefaultCreator = "Created by DeveParTools (https://github.com/devedse/DeveParTools)";

    private sealed class InputFile
    {
        public required string Path { get; init; }

        public required string Name { get; init; }

        public required long Length { get; init; }

        public Md5Hash Hash16k { get; set; }

        public Md5Hash FileId { get; set; }

        public int FirstSlice { get; set; }

        public int SliceCount { get; set; }

        public IncrementalHash? FileHasher { get; set; }

        public Md5Hash FileHash { get; set; }
    }

    private sealed record Volume(string Path, int FirstExponent, int Count);

    public static Par2CreateResult Create(Par2CreateOptions options, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var observer = context.Observer;
        var outputPath = Path.GetFullPath(options.OutputPath);
        var baseDir = Path.GetFullPath(options.BaseDirectory ?? Path.GetDirectoryName(outputPath)!);

        // 1. Inputs: names, lengths, MD5 of the first 16 KiB, File IDs. The same file given twice (also spelled
        //    differently, e.g. "x/../a" for "a", or on Windows with the base directory part in other letter case)
        //    gets the same stored name and is used once; different files always get different names (relative
        //    paths below one base directory).
        observer.Stage(Par2Stages.Preparing);
        var inputs = LoadInputs(options, baseDir);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("No input files.");
        }

        long sliceSize = options.SliceSize ?? ChooseSliceSize(inputs.Select(i => i.Length).ToList(), options.SliceCount);
        if (sliceSize <= 0 || sliceSize % 4 != 0)
        {
            throw new ArgumentException("Slice size must be a positive multiple of 4.");
        }

        var buf16 = new byte[16384];
        int prepared = 0;
        foreach (var f in inputs)
        {
            if (++prepared % 64 == 0)
            {
                observer.Progress(0.99 * prepared / inputs.Count);
            }

            using var fs = File.OpenRead(f.Path);
            int n = fs.ReadAtLeast(buf16, buf16.Length, throwOnEndOfStream: false);
            f.Hash16k = Md5Hash.Compute(buf16.AsSpan(0, n));
            f.FileId = Par2PacketWriter.ComputeFileId(f.Hash16k, f.Length, f.Name);
        }

        // 2. Empty files have no slices and go into the non-recovery set (like MultiPar): par2cmdline skips empty
        //    inputs when creating and reports an empty file of the recovery set as damaged when verifying. Their
        //    description packets still record them, so Verify checks them and Repair can recreate them.
        //    Slice numbering: files in File ID order, slices in file order.
        var empty = inputs.Where(i => i.Length == 0).OrderBy(i => i.FileId).ToList();
        foreach (var f in empty)
        {
            f.FileHash = f.Hash16k;
        }

        inputs = inputs.Where(i => i.Length > 0).OrderBy(i => i.FileId).ToList();
        int totalSlices = 0;
        foreach (var f in inputs)
        {
            f.FirstSlice = totalSlices;
            f.SliceCount = (int)((f.Length + sliceSize - 1) / sliceSize);
            totalSlices += f.SliceCount;
        }

        if (totalSlices > Par2Math.MaxInputSlices)
        {
            throw new ArgumentException($"{totalSlices} input slices exceed the PAR2 maximum of {Par2Math.MaxInputSlices}; use a larger slice size.");
        }

        int recoveryCount = RecoveryCountOf(totalSlices, options);
        if (recoveryCount < 0 || options.FirstExponent < 0 || (long)options.FirstExponent + recoveryCount > 65535)
        {
            throw new ArgumentException("Recovery slice exponents must lie in [0, 65535).");
        }

        var mainBody = Par2PacketWriter.MainBody(sliceSize, inputs.Select(i => i.FileId).ToList(), empty.Select(i => i.FileId).ToList());
        var setId = Md5Hash.Compute(mainBody);
        var map = context.CreateBlockMap(totalSlices, recoveryCount);
        foreach (var f in inputs)
        {
            for (int s = 0; s < f.SliceCount; s++)
            {
                long off = s * sliceSize;
                map.Ownership[f.FirstSlice + s] = [new BlockLocation(f.Path, off, (int)Math.Min(sliceSize, f.Length - off))];
            }
        }

        // 3. Volume files.
        var volumes = PlanVolumes(outputPath, options.Scheme, options.VolumeCount, options.FirstExponent, recoveryCount, sliceSize, inputs.Select(i => i.Length).DefaultIfEmpty(0).Max());
        if (!options.WriteIndexFile && volumes.FirstOrDefault(v => File.Exists(v.Path)) is { } existing)
        {
            throw new IOException($"'{existing.Path}' already exists.");
        }

        for (int v = 0; v < volumes.Count; v++)
        {
            for (int k = 0; k < volumes[v].Count; k++)
            {
                map.RecoveryContainer[volumes[v].FirstExponent - options.FirstExponent + k] = Path.GetFileName(volumes[v].Path);
            }
        }

        // 4. Hashes and recovery data. In a single pass every input byte is read once, in order, so the hashes are
        //    computed while encoding; otherwise a separate hashing pass runs first.
        var sliceMd5 = new IncrementalHash?[totalSlices];
        var sliceCrc = new uint[totalSlices];
        var sliceHashes = new Md5Hash[totalSlices];
        var combineOptions = context.Combine.Create(observer.ToProgress());
        int sliceLen = LinearCombiner.PlanSliceLength(2, totalSlices, recoveryCount, sliceSize, combineOptions);
        bool singlePass = sliceLen >= sliceSize;
        int passes = (int)((sliceSize + sliceLen - 1) / sliceLen);
        if (options.WriteIndexFile)
        {
            observer.Plan(Par2Stages.ForCreate(inputs.Count + empty.Count, inputs.Sum(i => i.Length), totalSlices, recoveryCount, sliceSize, passes, volumes.Count));
        }

        observer.Progress(1);

        using var handles = new FileHandleCache();
        if (!singlePass || recoveryCount == 0)
        {
            observer.Stage(Par2Stages.Hashing);
            HashInputs(inputs, sliceSize, sliceHashes, sliceCrc, handles, context, map);
        }

        var recoveryDone = new bool[recoveryCount];
        if (recoveryCount > 0)
        {
            observer.Stage(Par2Stages.Encoding);
            var volumeStreams = volumes.ToDictionary(v => v, v =>
                new FileStream(v.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess));
            try
            {
                // Recovery packet j (exponent FirstExponent + j) lives in its volume at DataOffset − header length.
                var outputs = new List<(FileStream Stream, long DataOffset, byte[] Header)>();
                foreach (var v in volumes)
                {
                    long pos = 0;
                    for (int k = 0; k < v.Count; k++)
                    {
                        var header = Par2PacketWriter.RecoverySliceHeader(setId, (uint)(v.FirstExponent + k), sliceSize);
                        outputs.Add((volumeStreams[v], pos + header.Length, header));
                        pos += header.Length + sliceSize;
                    }

                    volumeStreams[v].SetLength(pos);
                }

                var source = new SliceSource(inputs, sliceSize, handles, singlePass ? sliceMd5 : null, singlePass ? sliceCrc : null, map);
                var sink = new RecoverySink(outputs, sliceSize, recoveryDone, map);
                var exponents = Enumerable.Range(options.FirstExponent, recoveryCount).Select(e => (uint)e).ToArray();
                if (singlePass)
                {
                    foreach (var f in inputs)
                    {
                        f.FileHasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    }
                }

                LinearCombiner.Combine(new Gf16Field(Par2Math.Field), new Par2Coefficients(exponents), totalSlices, recoveryCount,
                    source, sink, sliceSize, combineOptions);

                if (singlePass)
                {
                    for (int i = 0; i < totalSlices; i++)
                    {
                        sliceHashes[i] = Md5Hash.FromBytes(sliceMd5[i]!.GetHashAndReset());
                        sliceMd5[i]!.Dispose();
                    }

                    foreach (var f in inputs)
                    {
                        f.FileHash = Md5Hash.FromBytes(f.FileHasher!.GetHashAndReset());
                        f.FileHasher.Dispose();
                    }
                }

                // Recovery packets whose MD5 could not be computed on the fly (multi-pass): read back. Progress of
                // this step: the packet headers are the first half, the copies of the critical packets the second.
                observer.Stage(Par2Stages.WritingRecovery);
                for (int j = 0; j < recoveryCount; j++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (!recoveryDone[j])
                    {
                        FinishRecoveryPacketFromDisk(outputs[j].Stream, outputs[j].DataOffset, outputs[j].Header, sliceSize);
                    }

                    RandomAccess.Write(outputs[j].Stream.SafeFileHandle, outputs[j].Header, outputs[j].DataOffset - outputs[j].Header.Length);
                    map.SetRecovery(j, RecoveryBlockState.Created);
                    if (j % 64 == 63)
                    {
                        observer.Progress(0.5 * (j + 1) / recoveryCount);
                    }
                }
            }
            finally
            {
                foreach (var s in volumeStreams.Values)
                {
                    s.Dispose();
                }
            }
        }

        // 5. Critical packets: main, file descriptions, slice checksums, creator (and comment).
        var critical = new List<byte[]> { Par2PacketWriter.Build(setId, Par2PacketTypes.Main, mainBody) };
        foreach (var f in inputs)
        {
            critical.Add(Par2PacketWriter.FileDescription(setId, f.FileId, f.FileHash, f.Hash16k, f.Length, f.Name));
            var slices = new List<(Md5Hash, uint)>(f.SliceCount);
            for (int s = 0; s < f.SliceCount; s++)
            {
                slices.Add((sliceHashes[f.FirstSlice + s], sliceCrc[f.FirstSlice + s]));
            }

            critical.Add(Par2PacketWriter.SliceChecksum(setId, f.FileId, slices));
        }

        foreach (var f in empty)
        {
            critical.Add(Par2PacketWriter.FileDescription(setId, f.FileId, f.FileHash, f.Hash16k, 0, f.Name));
        }

        if (!string.IsNullOrEmpty(options.Comment))
        {
            critical.Add(Par2PacketWriter.AsciiComment(setId, options.Comment));
        }

        var creator = Par2PacketWriter.Creator(setId, options.Creator);

        int copied = 0;
        foreach (var v in volumes)
        {
            using var fs = new FileStream(v.Path, FileMode.Open, FileAccess.Write);
            fs.Seek(0, SeekOrigin.End);
            int copies = CriticalCopies(v.Count);
            for (int c = 0; c < copies; c++)
            {
                foreach (var p in critical)
                {
                    fs.Write(p);
                }
            }

            fs.Write(creator);
            observer.Progress(0.5 + 0.5 * ++copied / volumes.Count);
        }

        if (options.WriteIndexFile)
        {
            observer.Stage(Par2Stages.WritingIndex);
            using var index = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            foreach (var p in critical)
            {
                index.Write(p);
            }

            index.Write(creator);
        }

        observer.Progress(1);
        return new Par2CreateResult
        {
            SetId = setId,
            SliceSize = sliceSize,
            InputSlices = totalSlices,
            RecoverySlices = recoveryCount,
            WrittenFiles = options.WriteIndexFile ? [outputPath, .. volumes.Select(v => v.Path)] : volumes.Select(v => v.Path).ToList(),
        };
    }

    /// <summary>
    /// The files <see cref="Create"/> writes and their exact sizes, computed without reading the inputs (the sizes do
    /// not depend on the contents).
    /// </summary>
    /// <remarks>
    /// <para>Every packet is a 64-byte header plus its body, padded to a multiple of 4. The critical packets are the
    /// main packet (12 bytes + 16 per file), a file description per file (56 bytes + the name), a slice checksum packet
    /// per non-empty file (16 bytes + 20 per slice: MD5 and CRC32) and the comment.</para>
    /// <para>The index file holds the critical packets once plus the creator packet. A volume holds its recovery slices
    /// (64 + 4 + slice size each), 1 + log2(slices) copies of the critical packets (<see cref="CriticalCopies"/>) and the
    /// creator packet. Example: 1,000 slices of 4 KiB over 10 files cost 10 × 20,000 bytes of slice checksums, so every
    /// copy of the critical packets is about 21 KB, and a volume with 64 slices carries 7 copies (147 KB) next to its
    /// 266 KB of recovery data.</para>
    /// </remarks>
    public static IReadOnlyList<OutputFileSize> PlanFileSizes(Par2CreateOptions options)
    {
        var outputPath = Path.GetFullPath(options.OutputPath);
        var baseDir = Path.GetFullPath(options.BaseDirectory ?? Path.GetDirectoryName(outputPath)!);
        var inputs = LoadInputs(options, baseDir);
        var nonEmpty = inputs.Where(i => i.Length > 0).ToList();
        long sliceSize = options.SliceSize ?? ChooseSliceSize(inputs.Select(i => i.Length).ToList(), options.SliceCount);
        long SlicesOf(InputFile f) => (f.Length + sliceSize - 1) / sliceSize;
        int totalSlices = (int)nonEmpty.Sum(SlicesOf);
        int recoveryCount = RecoveryCountOf(totalSlices, options);

        long critical = Par2PacketWriter.Build(default, Par2PacketTypes.Main, Par2PacketWriter.MainBody(sliceSize, nonEmpty.Select(_ => default(Md5Hash)).ToList(), inputs.Where(i => i.Length == 0).Select(_ => default(Md5Hash)).ToList())).Length;
        foreach (var f in inputs)
        {
            critical += Par2PacketWriter.FileDescription(default, default, default, default, f.Length, f.Name).Length;
            if (f.Length > 0)
            {
                critical += Par2PacketTypes.HeaderSize + 16 + 20 * SlicesOf(f); // slice checksums: always a multiple of 4
            }
        }

        if (!string.IsNullOrEmpty(options.Comment))
        {
            critical += Par2PacketWriter.AsciiComment(default, options.Comment).Length;
        }

        long creator = Par2PacketWriter.Creator(default, options.Creator).Length;
        long sliceHeader = Par2PacketWriter.RecoverySliceHeader(default, 0, sliceSize).Length;
        var sizes = new List<OutputFileSize>();
        if (options.WriteIndexFile)
        {
            sizes.Add(new OutputFileSize(outputPath, critical + creator, 0));
        }

        foreach (var v in PlanVolumes(outputPath, options.Scheme, options.VolumeCount, options.FirstExponent, recoveryCount, sliceSize, nonEmpty.Select(i => i.Length).DefaultIfEmpty(0).Max()))
        {
            sizes.Add(new OutputFileSize(v.Path, v.Count * (sliceHeader + sliceSize) + CriticalCopies(v.Count) * critical + creator, v.Count));
        }

        return sizes;
    }

    /// <summary>More recovery slices in a volume → more copies of the critical packets: 1 + log2(slices).</summary>
    internal static int CriticalCopies(int slices) => 1 + (int)Math.Log2(Math.Max(1, slices));

    /// <summary>The recovery slice count: the exact count if given, else the redundancy of the input slices (none without input).</summary>
    private static int RecoveryCountOf(int totalSlices, Par2CreateOptions options)
        => totalSlices == 0 ? 0 : options.RecoverySliceCount ?? (int)Math.Ceiling(totalSlices * options.RedundancyPercent / 100.0);

    /// <summary>
    /// The inputs with their stored names and lengths. The same file given twice (also spelled differently, e.g.
    /// "x/../a" for "a") gets the same stored name and is used once.
    /// </summary>
    private static List<InputFile> LoadInputs(Par2CreateOptions options, string baseDir) => options.InputFiles
        .Select(p => Path.GetFullPath(p))
        .Select(p => new InputFile { Path = p, Name = PathSafety.ToStoredName(baseDir, p), Length = new FileInfo(p).Length })
        .DistinctBy(i => i.Name, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Smallest slice size (multiple of 4) for which the slice count (each file rounded up separately) does not
    /// exceed <paramref name="targetCount"/> (and 32768). When even one slice per file is more than the target,
    /// the smallest size that gives one slice per file.
    /// </summary>
    /// <remarks>
    /// The count only falls as the size grows, so the answer lies between <c>lo = total / target</c> (any smaller size
    /// needs more than target slices) and <c>hi</c> = the largest file (one slice per file, the fewest possible), and
    /// is found by bisection. Examples: one file of 1001 bytes, target 10 → lo = 100 gives 11 slices, so the answer is
    /// 104 (10 slices). Three files of 10 bytes, target 1 → hi = 12 (3 slices; 28 = total / target would also give 3
    /// slices, but larger recovery slices).
    /// </remarks>
    public static long ChooseSliceSize(IReadOnlyList<long> lengths, int targetCount)
    {
        targetCount = Math.Clamp(targetCount, 1, Par2Math.MaxInputSlices);
        long total = lengths.Sum();
        if (total == 0)
        {
            return 4;
        }

        long Count(long size) => lengths.Sum(l => (l + size - 1) / size);
        long lo = Math.Max(4, (total / targetCount) / 4 * 4);
        long hi = Math.Max(4, (lengths.Max() + 3) / 4 * 4);
        if (lo >= hi)
        {
            return hi;
        }

        if (Count(lo) <= targetCount)
        {
            return lo;
        }

        while (hi - lo > 4)
        {
            long mid = (lo + hi) / 2 / 4 * 4;
            if (Count(mid) <= targetCount)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return hi;
    }

    /// <summary>
    /// Distributes the recovery slices over volume files (see <see cref="RecoveryFileSizing"/>) and names them like
    /// par2cmdline: "set.vol{first exponent}+{count}.par2", both numbers zero-padded to the widest value that occurs
    /// (the exponent after the last file included).
    /// </summary>
    /// <remarks>
    /// 10 recovery slices from exponent 0, exponential: set.vol00+1.par2, set.vol01+2.par2, set.vol03+4.par2,
    /// set.vol07+3.par2. Used by <see cref="Create"/> and, to preview a set before creating it, by
    /// <c>ParClient.Plan</c>.
    /// </remarks>
    /// <param name="outputPath">The index file, e.g. "/data/set.par2".</param>
    /// <param name="largestFile">Length of the largest input file (for <see cref="RecoveryFileScheme.LimitSize"/>).</param>
    public static IReadOnlyList<RecoveryFilePlan> PlanRecoveryFiles(string outputPath, RecoveryFileScheme scheme, int? volumeCount, int firstExponent, int recoveryCount, long sliceSize, long largestFile)
        => PlanVolumes(outputPath, scheme, volumeCount, firstExponent, recoveryCount, sliceSize, largestFile)
            .Select(v => new RecoveryFilePlan(v.Path, v.FirstExponent, v.Count)).ToList();

    private static List<Volume> PlanVolumes(string outputPath, RecoveryFileScheme scheme, int? volumeCount, int firstExponent, int recoveryCount, long sliceSize, long largestFile)
    {
        var volumes = new List<Volume>();
        var counts = RecoveryFileSizing.Counts(recoveryCount, scheme, volumeCount, Math.Max(1, largestFile / sliceSize));
        if (counts.Count == 0)
        {
            return volumes;
        }

        var baseName = outputPath.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) ? outputPath[..^5] : outputPath;
        int lowDigits = Digits(firstExponent + recoveryCount);
        int countDigits = Digits(counts.Max());
        int exp = firstExponent;
        foreach (var c in counts)
        {
            var name = $"{baseName}.vol{exp.ToString(CultureInfo.InvariantCulture).PadLeft(lowDigits, '0')}+{c.ToString(CultureInfo.InvariantCulture).PadLeft(countDigits, '0')}.par2";
            volumes.Add(new Volume(name, exp, c));
            exp += c;
        }

        return volumes;
    }

    private static int Digits(int v) => v.ToString(CultureInfo.InvariantCulture).Length;

    private static void HashInputs(List<InputFile> inputs, long sliceSize, Md5Hash[] sliceHashes, uint[] sliceCrc, FileHandleCache handles, OperationContext context, BlockStateMap map)
    {
        long done = 0;
        long total = Math.Max(1, inputs.Sum(i => i.Length));
        Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Threads), CancellationToken = context.CancellationToken }, f =>
        {
            using var fileMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buf = new byte[sliceSize];
            for (int s = 0; s < f.SliceCount; s++)
            {
                int n = handles.ReadPadded(f.Path, s * sliceSize, buf);
                fileMd5.AppendData(buf, 0, n);
                sliceHashes[f.FirstSlice + s] = Md5Hash.Compute(buf);
                sliceCrc[f.FirstSlice + s] = Crc32Ieee.Compute(buf);
                map.Set(f.FirstSlice + s, BlockState.Processed);
                context.Observer.Progress((double)Interlocked.Add(ref done, n) / total, "Hashing");
            }

            f.FileHash = Md5Hash.FromBytes(fileMd5.GetHashAndReset());
        });
    }

    private static void FinishRecoveryPacketFromDisk(FileStream stream, long dataOffset, byte[] header, long sliceSize)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(header, 32, header.Length - 32);
        var buf = new byte[(int)Math.Min(sliceSize, 1 << 20)];
        long left = sliceSize;
        long off = dataOffset;
        while (left > 0)
        {
            int n = (int)Math.Min(left, buf.Length);
            FileHandleCache.ReadPadded(stream.SafeFileHandle, off, buf.AsSpan(0, n));
            md5.AppendData(buf, 0, n);
            off += n;
            left -= n;
        }

        md5.GetHashAndReset().CopyTo(header, 16);
    }

    /// <summary>Coefficient (j, i) = base_i ^ exponent_j.</summary>
    private sealed class Par2Coefficients(uint[] exponents) : ICoefficientProvider<ushort>
    {
        public void Fill(int output, int firstInput, Span<ushort> destination)
        {
            uint e = exponents[output];
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = Par2Math.Coefficient(firstInput + i, e);
            }
        }
    }

    /// <summary>Reads input slices (zero-padded) and, in single-pass mode, hashes them on the way.</summary>
    private sealed class SliceSource : IRegionSource
    {
        private readonly List<InputFile> _inputs;
        private readonly long _sliceSize;
        private readonly FileHandleCache _handles;
        private readonly IncrementalHash?[]? _sliceMd5;
        private readonly uint[]? _sliceCrc;
        private readonly BlockStateMap _map;
        private readonly int[] _fileOfSlice;

        public SliceSource(List<InputFile> inputs, long sliceSize, FileHandleCache handles, IncrementalHash?[]? sliceMd5, uint[]? sliceCrc, BlockStateMap map)
        {
            _inputs = inputs;
            _sliceSize = sliceSize;
            _handles = handles;
            _sliceMd5 = sliceMd5;
            _sliceCrc = sliceCrc;
            _map = map;
            _fileOfSlice = new int[inputs.Sum(i => i.SliceCount)];
            for (int f = 0; f < inputs.Count; f++)
            {
                for (int s = 0; s < inputs[f].SliceCount; s++)
                {
                    _fileOfSlice[inputs[f].FirstSlice + s] = f;
                }
            }
        }

        public void Read(int index, long offset, Span<byte> destination)
        {
            var f = _inputs[_fileOfSlice[index]];
            long fileOffset = (index - f.FirstSlice) * _sliceSize + offset;
            long available = Math.Clamp(f.Length - fileOffset, 0, destination.Length);
            int n = _handles.ReadPadded(f.Path, fileOffset, destination[..(int)available]);
            destination[n..].Clear();
            if (_sliceMd5 != null)
            {
                var h = _sliceMd5[index] ??= IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                h.AppendData(destination);
                _sliceCrc![index] = Crc32Ieee.Compute(destination);
                f.FileHasher!.AppendData(destination[..n]);
            }

            _map.Set(index, BlockState.Processed);
        }
    }

    /// <summary>Writes recovery data into the volume files and, when a whole slice arrives at once, its packet MD5.</summary>
    private sealed class RecoverySink(List<(FileStream Stream, long DataOffset, byte[] Header)> outputs, long sliceSize, bool[] done, BlockStateMap map) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source)
        {
            var (stream, dataOffset, header) = outputs[index];
            RandomAccess.Write(stream.SafeFileHandle, source, dataOffset + offset);
            if (offset == 0 && source.Length == sliceSize)
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                md5.AppendData(header, 32, header.Length - 32);
                md5.AppendData(source);
                md5.GetHashAndReset().CopyTo(header, 16);
                done[index] = true;
            }

            map.SetRecovery(index, RecoveryBlockState.Created);
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Core.Util;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

public enum Par3Code
{
    /// <summary>Cauchy for small sets, FFT for larger ones.</summary>
    Auto,

    /// <summary>Cauchy matrix (Reed-Solomon, readable by every PAR3 client).</summary>
    Cauchy,

    /// <summary>par3cmdline's FFT Reed-Solomon (Leopard). Much faster for large sets.</summary>
    Fft,
}

public enum Par3Dedup
{
    None,

    /// <summary>Identical block-aligned blocks (in the same or different files) are stored once.</summary>
    Blocks,
}

public sealed class Par3CreateOptions
{
    /// <summary>Files and/or directories to protect.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Path of the index file, e.g. "/data/set.par3".</summary>
    public required string OutputPath { get; init; }

    /// <summary>Directory names are relative to (default: directory of <see cref="OutputPath"/>).</summary>
    public string? BaseDirectory { get; init; }

    public bool Recursive { get; init; } = true;

    /// <summary>Block size in bytes (a multiple of 8). When null it is derived from <see cref="BlockCount"/>.</summary>
    public ulong? BlockSize { get; init; }

    public int BlockCount { get; init; } = 2000;

    public int? RecoveryBlockCount { get; init; }

    public double RedundancyPercent { get; init; } = 10;

    public ulong FirstRecoveryBlock { get; init; }

    /// <summary>FFT only: the most recovery blocks this set may ever get (later "extend" can add up to it).</summary>
    public int? MaxRecoveryBlocks { get; init; }

    public Par3Code Code { get; init; } = Par3Code.Auto;

    public Par3Dedup Dedup { get; init; } = Par3Dedup.None;

    /// <summary>Pack file tails of 40+ bytes together into shared blocks.</summary>
    public bool PackTails { get; init; } = true;

    /// <summary>Also store the input data in the PAR3 files ("archive" mode, .partXX+YY.par3 files).</summary>
    public bool StoreData { get; init; }

    /// <summary>Number of recovery files; null = automatic (see <see cref="Scheme"/>).</summary>
    public int? VolumeCount { get; init; }

    /// <summary>How the recovery blocks are spread over the recovery files (the same rules as PAR2).</summary>
    public RecoveryFileScheme Scheme { get; init; } = RecoveryFileScheme.Exponential;

    public string? Comment { get; init; }

    public string Creator { get; init; } = Par3Creator.DefaultCreator;

    public bool UnixPermissions { get; init; }

    public bool FatPermissions { get; init; }

    /// <summary>Derive the unique number from the contents so identical inputs give identical files.</summary>
    public bool Reproducible { get; init; }

    /// <summary>FFT only: number of extra cohorts (0 = automatic).</summary>
    public int Interleave { get; init; }

    /// <summary>Cauchy x_I convention (par3cmdline-compatible by default).</summary>
    public Par3CauchyConvention CauchyConvention { get; init; } = Par3CauchyConvention.Par3cmdline;
}

/// <summary>The steps of creating (and extending) a PAR3 set, with their names and what they do.</summary>
public static class Par3Stages
{
    public const string Preparing = "Preparing";
    public const string Hashing = "Hashing";
    public const string Metadata = "Writing metadata";
    public const string Encoding = "Encoding";
    public const string WritingRecovery = "Writing recovery files";
    public const string StoringData = "Storing data";
    public const string WritingIndex = "Writing the index file";

    /// <summary>What <see cref="Encoding"/> does, for the code that is used.</summary>
    public static string EncodingDescription(bool fft, int recoveryBlocks) => fft
        ? $"Reads all input blocks again and computes the {recoveryBlocks:N0} recovery blocks with the FFT Reed-Solomon code, one group of blocks (cohort) at a time. The recovery files were created at their final size when this step started; they are filled in now."
        : $"Reads all input blocks again and computes the {recoveryBlocks:N0} recovery blocks with the Cauchy Reed-Solomon code: every recovery block is a different weighted sum of all input blocks, calculated in a Galois field. The recovery files were created at their final size when this step started; they are filled in now.";

    public const string WritingRecoveryDescription = "Completes the checksum of every recovery block and writes copies of the set's description (file list and block checksums) into every recovery file, so each recovery file can also be used on its own.";

    /// <summary>
    /// The steps of <see cref="Par3Creator.Create"/>, weighted by the bytes each one reads or writes: hashing reads the
    /// input once, encoding reads it again and writes the recovery blocks, the recovery files get copies of the set's
    /// description (about 24 bytes per input block each), storing data reads and writes the input.
    /// </summary>
    internal static IReadOnlyList<OperationStage> ForCreate(long inputBytes, int files, ulong blocks, int recovery, long blockSize, bool fft, int recoveryFiles, bool storeData)
    {
        double description = blocks * 24.0 + files * 300.0;
        int copies = recovery == 0 ? 0 : Par3Creator.VitalCopiesPerFile((int)(blocks / 1024) + files + 4, recovery, blocks, Math.Max(1, recoveryFiles));
        var stages = new List<OperationStage>
        {
            new(Preparing, files * 512.0 + 4096, "Lists the files and folders to protect and chooses the block size."),
            new(Hashing, inputBytes, "Reads every file once and computes its checksums: one for every block (CRC-64 and BLAKE3), one for the last piece of each file and one for the whole file. Verify and repair use them to find good, moved and damaged blocks."),
            new(Metadata, description, "Builds the description of the set (the vital packets): the list of files and folders, the checksum of every block and the parameters of the code."),
        };
        if (recovery > 0)
        {
            stages.Add(new(Encoding, inputBytes + (double)recovery * blockSize, EncodingDescription(fft, recovery)));
            stages.Add(new(WritingRecovery, description * copies * recoveryFiles + recovery * 128.0, WritingRecoveryDescription));
        }

        if (storeData)
        {
            stages.Add(new(StoringData, inputBytes * 2.0, "Copies the contents of your files into the .part files (archive mode), so the set alone can recreate them."));
        }

        stages.Add(new(WritingIndex, description, "Writes the index file: the description of the set, once."));
        return stages;
    }
}

public sealed class Par3CreateResult
{
    public required InputSetId SetId { get; init; }

    public required ulong BlockSize { get; init; }

    public required ulong InputBlocks { get; init; }

    public required int RecoveryBlocks { get; init; }

    public required string CodeDescription { get; init; }

    public required IReadOnlyList<string> WrittenFiles { get; init; }

    /// <summary>Set by <see cref="Par3Creator.PlanFileSizes"/>: the files and sizes that would be written.</summary>
    internal IReadOnlyList<OutputFileSize>? PlannedSizes { get; init; }
}

/// <summary>Creates PAR 3.0 recovery sets.</summary>
public static class Par3Creator
{
    public const string DefaultCreator = "DeveParTools (https://github.com/devedse/DeveParTools)";

    private sealed class FileInfoEntry
    {
        public required string FullPath { get; init; }

        public required string RelativePath { get; init; }

        public required long Length { get; init; }

        public Fingerprint FileHash { get; set; }

        public ulong Hash16k { get; set; }

        public BlockChecksum[] BlockChecks { get; set; } = [];

        public byte[]? InlineTail { get; set; }

        public ulong TailCrc { get; set; }

        public Fingerprint TailHash { get; set; }

        public ulong TailBlock { get; set; }

        public ulong TailOffset { get; set; }

        public ulong[] BlockIndices { get; set; } = [];

        public List<Par3Chunk> Chunks { get; } = new();

        public Fingerprint PacketChecksum { get; set; }

        public byte[] Packet { get; set; } = [];

        public List<byte[]> OptionPackets { get; } = new();
    }

    public static Par3CreateResult Create(Par3CreateOptions options, OperationContext? context = null) => Run(options, context, sizesOnly: false);

    /// <summary>
    /// The files <see cref="Create"/> writes and their exact sizes, without reading the input files: the same steps
    /// build the same packets, with zero checksums (the sizes do not depend on them), and then the sizes are added up
    /// instead of written. Deduplication needs the contents, so it is left out (with it the set can be smaller).
    /// </summary>
    /// <remarks>
    /// <para>The index file holds every vital packet once (Start, Matrix, the block checksums in External Data packets,
    /// options, File, Directory and Root packets, the comment) plus the creator. A recovery file holds its recovery
    /// blocks (an 88-byte packet header + the block each), copies of all vital packets (see <see cref="VitalCopiesPerFile"/>)
    /// and the creator. The block checksums are 24 bytes per input block, and every recovery file carries them: with
    /// 32,000 blocks of 216 bytes that is 768 KB per copy, which dwarfs a recovery file of a few blocks.</para>
    /// </remarks>
    public static IReadOnlyList<OutputFileSize> PlanFileSizes(Par3CreateOptions options) => Run(options, null, sizesOnly: true).PlannedSizes!;

    private static Par3CreateResult Run(Par3CreateOptions options, OperationContext? context, bool sizesOnly)
    {
        context ??= OperationContext.Default;
        var observer = context.Observer;
        var token = context.CancellationToken;
        var outputPath = Path.GetFullPath(options.OutputPath);
        var baseDir = Path.GetFullPath(options.BaseDirectory ?? Path.GetDirectoryName(outputPath)!);

        // 1. Input tree.
        observer.Stage(Par3Stages.Preparing);
        var (files, directories) = Enumerate(options, baseDir);
        if (files.Count == 0 && directories.Count == 0)
        {
            throw new ArgumentException("No input files.");
        }

        long total = files.Sum(f => f.Length);
        ulong blockSize = options.BlockSize ?? ChooseBlockSize(files.Select(f => f.Length).ToList(), options.BlockCount, options.PackTails);
        if (blockSize == 0 || blockSize % 8 != 0)
        {
            throw new ArgumentException("Block size must be a positive multiple of 8.");
        }

        if (!sizesOnly)
        {
            // Every step, weighted by its work (block and recovery counts as planned; deduplication can lower them).
            ulong plannedBlocks = CountBlocks(files.Select(f => f.Length), blockSize, options.PackTails);
            int plannedRecovery = plannedBlocks == 0 ? 0 : options.RecoveryBlockCount ?? (int)Math.Ceiling(plannedBlocks * options.RedundancyPercent / 100.0);
            int plannedFiles = PlanVolumes(outputPath, options.Scheme, options.VolumeCount, options.FirstRecoveryBlock, plannedRecovery, ".vol", LimitBlocks(files.Select(f => f.Length).DefaultIfEmpty(0).Max(), blockSize)).Count;
            observer.Plan(Par3Stages.ForCreate(total, files.Count, plannedBlocks, plannedRecovery, (long)blockSize, ResolveCode(options.Code, plannedBlocks, plannedRecovery) == Par3Code.Fft, plannedFiles, options.StoreData));
            observer.Progress(1);
        }

        // 2. Hash every file once: block checksums, tails, file hash, 16 KiB rolling hash. (Sizes only: just the shapes.)
        if (sizesOnly)
        {
            foreach (var f in files)
            {
                ulong tail = (ulong)f.Length % blockSize;
                f.BlockChecks = new BlockChecksum[(ulong)f.Length / blockSize];
                f.InlineTail = tail is > 0 and < Par3Chunk.InlineLimit ? new byte[tail] : null;
            }
        }
        else
        {
            observer.Stage(Par3Stages.Hashing);
            HashFiles(files, blockSize, context);
        }

        // 3. Block numbering (with optional deduplication) and tail packing.
        var blockPieces = new List<List<(ulong BlockOffset, ulong Length, PieceSource Source)>>();
        var blockChecks = new List<BlockChecksum?>();
        var dedup = new Dictionary<Fingerprint, ulong>();
        foreach (var f in files)
        {
            int full = (int)((ulong)f.Length / blockSize);
            f.BlockIndices = new ulong[full];
            for (int b = 0; b < full; b++)
            {
                var check = f.BlockChecks[b];
                if (options.Dedup == Par3Dedup.Blocks && !sizesOnly && dedup.TryGetValue(check.Hash, out var existing) && blockChecks[(int)existing]!.Value.Crc64 == check.Crc64)
                {
                    f.BlockIndices[b] = existing;
                    continue;
                }

                ulong idx = (ulong)blockPieces.Count;
                blockPieces.Add([(0, blockSize, new PieceSource(f.FullPath, b * (long)blockSize))]);
                blockChecks.Add(check);
                dedup.TryAdd(check.Hash, idx);
                f.BlockIndices[b] = idx;
            }
        }

        // Tails of 40+ bytes go into blocks (packed first-fit when enabled, exactly as CountBlocks counts them).
        var packer = new FirstFitBlocks(blockSize);
        var tailBlocks = new List<ulong>();
        foreach (var f in files.OrderByDescending(f => (ulong)f.Length % blockSize))
        {
            ulong tail = (ulong)f.Length % blockSize;
            if (tail < Par3Chunk.InlineLimit)
            {
                continue;
            }

            var (slot, used) = options.PackTails ? packer.Place(tail) : (tailBlocks.Count, 0UL);
            if (slot == tailBlocks.Count)
            {
                tailBlocks.Add((ulong)blockPieces.Count);
                blockPieces.Add(new List<(ulong, ulong, PieceSource)>());
                blockChecks.Add(null);
            }

            ulong tailIdx = tailBlocks[slot];
            f.TailBlock = tailIdx;
            f.TailOffset = used;
            blockPieces[(int)tailIdx].Add((used, tail, new PieceSource(f.FullPath, f.Length - (long)tail)));
        }

        ulong blockCount = (ulong)blockPieces.Count;
        using var handles = new FileHandleCache();
        var pieceArray = blockPieces.Select(p => (IReadOnlyList<(ulong, ulong, PieceSource)>)p).ToArray();
        var blockSource = new Par3BlockSource(pieceArray, handles);

        // Checksums of tail-packed blocks (the tails at their offsets, zeros elsewhere).
        var buf = new byte[blockSize];
        for (int i = 0; i < blockChecks.Count; i++)
        {
            if (blockChecks[i] == null)
            {
                if (!sizesOnly)
                {
                    blockSource.Read(i, 0, buf);
                }

                blockChecks[i] = sizesOnly ? default : new BlockChecksum(Crc64Iso.Compute(buf), Fingerprint.Compute(buf));
            }
        }

        // Chunks: maximal runs of consecutive block indices, the last one carries the tail.
        foreach (var f in files)
        {
            BuildChunks(f, blockSize);
        }

        // 4. Recovery parameters, field and code.
        int recoveryCount = blockCount == 0 ? 0 : options.RecoveryBlockCount ?? (int)Math.Ceiling(blockCount * options.RedundancyPercent / 100.0);
        if (recoveryCount < 0)
        {
            throw new ArgumentException("Recovery block count must not be negative.");
        }

        var code = ResolveCode(options.Code, blockCount, recoveryCount);

        if (code == Par3Code.Fft && (ulong)Math.Max(recoveryCount, options.MaxRecoveryBlocks ?? 0) > blockCount)
        {
            throw new ArgumentException("The FFT code needs at least as many input blocks as recovery blocks; use the Cauchy code.");
        }

        int maxRecovery = Math.Max(recoveryCount + (int)options.FirstRecoveryBlock, options.MaxRecoveryBlocks ?? 0);
        var (fftLayout, generator) = ChooseField(code, blockCount, recoveryCount, options.FirstRecoveryBlock, maxRecovery, options.Interleave);

        // 5. Packets.
        observer.Stage(Par3Stages.Metadata);
        ulong unique = sizesOnly ? 0 : options.Reproducible ? ReproducibleUnique(files, blockSize, generator) : BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var startBody = Par3PacketWriter.StartBody(default, default, blockSize, generator);
        var setId = Par3PacketWriter.ComputeSetId(unique, startBody);
        var vital = new List<byte[]>
        {
            Par3PacketWriter.Build(setId, Par3PacketTypes.Start, startBody),
        };

        byte[] matrixPacket = fftLayout != null
            ? Par3PacketWriter.Fft(setId, 0, 0, (byte)LeopardLog2(fftLayout.M), (uint)(fftLayout.Cohorts - 1))
            : Par3PacketWriter.Cauchy(setId, 0, 0, (ulong)maxRecovery);
        var matrixChecksum = Par3PacketWriter.ChecksumOf(matrixPacket);
        vital.Add(matrixPacket);

        // External data packets (block checksums), at most 1024 blocks per packet.
        for (ulong first = 0; first < blockCount; first += 1024)
        {
            var slice = blockChecks.Skip((int)first).Take((int)Math.Min(1024, blockCount - first)).Select(c => c!.Value).ToList();
            vital.Add(Par3PacketWriter.ExternalData(setId, first, slice));
        }

        // Option packets, file packets, directory packets (bottom-up) and the root.
        var optionCache = new Dictionary<Fingerprint, byte[]>();
        foreach (var f in files)
        {
            var opts = MakeOptions(setId, f.FullPath, options, optionCache);
            var name = Path.GetFileName(f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            f.Packet = Par3PacketWriter.File(setId, name, f.Hash16k, f.FileHash, opts, f.Chunks, blockSize);
            f.PacketChecksum = Par3PacketWriter.ChecksumOf(f.Packet);
            vital.Add(f.Packet);
        }

        var dirChecksums = new Dictionary<string, Fingerprint>(StringComparer.Ordinal);
        foreach (var dir in directories.OrderByDescending(d => d.Count(ch => ch == '/')).ThenBy(d => d, StringComparer.Ordinal))
        {
            var children = ChildrenOf(dir, files, dirChecksums);
            var opts = MakeOptions(setId, Path.Combine(baseDir, dir.Replace('/', Path.DirectorySeparatorChar)), options, optionCache, isDirectory: true);
            var packet = Par3PacketWriter.Directory(setId, dir[(dir.LastIndexOf('/') + 1)..], opts, children);
            dirChecksums[dir] = Par3PacketWriter.ChecksumOf(packet);
            vital.Add(packet);
        }

        var rootPacket = Par3PacketWriter.Root(setId, blockCount, false, [], ChildrenOf(string.Empty, files, dirChecksums));
        var rootChecksum = Par3PacketWriter.ChecksumOf(rootPacket);
        vital.Add(rootPacket);
        vital.InsertRange(1, optionCache.Values);
        if (!string.IsNullOrEmpty(options.Comment))
        {
            vital.Add(Par3PacketWriter.Text(setId, Par3PacketTypes.Comment, options.Comment));
        }

        var creatorPacket = Par3PacketWriter.Text(setId, Par3PacketTypes.Creator, options.Creator);

        // 6. Recovery data.
        var volumes = PlanVolumes(outputPath, options.Scheme, options.VolumeCount, options.FirstRecoveryBlock, recoveryCount, ".vol", LimitBlocks(files.Select(f => f.Length).DefaultIfEmpty(0).Max(), blockSize));
        if (sizesOnly)
        {
            long vitalBytes = vital.Sum(p => (long)p.Length);
            long creatorBytes = creatorPacket.Length;
            var sizes = new List<OutputFileSize> { new(outputPath, vitalBytes + creatorBytes, 0) };
            if (recoveryCount > 0)
            {
                int copies = VitalCopiesPerFile(vital.Count, recoveryCount, blockCount, volumes.Count);
                long header = Par3PacketWriter.RecoveryDataHeader(setId, rootChecksum, matrixChecksum, 0, (long)blockSize).Length;
                sizes.AddRange(volumes.Select(v => new OutputFileSize(v.Path, v.Count * (header + (long)blockSize) + copies * vitalBytes + creatorBytes, v.Count)));
            }

            if (options.StoreData && blockCount > 0)
            {
                long header = Par3PacketWriter.DataHeader(setId, 0, (long)blockSize).Length;
                sizes.AddRange(PlanDataParts(outputPath, options, blockCount).Select(p => new OutputFileSize(p.Path, p.Count * (header + (long)blockSize) + vitalBytes + creatorBytes, 0)));
            }

            return new Par3CreateResult { SetId = setId, BlockSize = blockSize, InputBlocks = blockCount, RecoveryBlocks = recoveryCount, CodeDescription = Describe(fftLayout, generator), WrittenFiles = [], PlannedSizes = sizes };
        }

        observer.Progress(1);
        var map = context.CreateBlockMap((int)blockCount, recoveryCount);
        var written = new List<string> { outputPath };
        var recoveryIndices = Enumerable.Range(0, recoveryCount).Select(i => options.FirstRecoveryBlock + (ulong)i).ToList();
        if (recoveryCount > 0)
        {
            observer.Stage(Par3Stages.Encoding);
            WriteRecovery(setId, rootChecksum, matrixChecksum, generator, fftLayout, blockCount, recoveryIndices, volumes, blockSource, (long)blockSize, vital, creatorPacket, options, context, map);
            written.AddRange(volumes.Select(v => v.Path));
        }

        // 7. Data packets (archive mode).
        if (options.StoreData && blockCount > 0)
        {
            written.AddRange(WriteDataPackets(setId, outputPath, blockCount, blockSource, blockSize, vital, creatorPacket, options, observer));
        }

        // 8. Index file: every vital packet once, plus the creator.
        observer.Stage(Par3Stages.WritingIndex);
        using (var index = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            foreach (var p in vital)
            {
                index.Write(p);
            }

            index.Write(creatorPacket);
        }

        observer.Progress(1);
        return new Par3CreateResult
        {
            SetId = setId,
            BlockSize = blockSize,
            InputBlocks = blockCount,
            RecoveryBlocks = recoveryCount,
            CodeDescription = Describe(fftLayout, generator),
            WrittenFiles = written,
        };
    }

    /// <summary>
    /// The field and layout of the code: for FFT the cohorts and Leopard field (GF(2^8), GF(2^16), or XOR when only one
    /// recovery block can ever exist), otherwise the smallest Cauchy field in which input and recovery indices cannot
    /// collide: x_I = I+1 and y_R = MAX−R must differ, so blocks + recovery ≤ 2^bits − 1 (GF(2^8) up to 255,
    /// GF(2^16) up to 65,535, then GF(2^32)).
    /// </summary>
    /// <param name="code">The resolved code (<see cref="ResolveCode"/>).</param>
    /// <param name="maxRecovery">The most recovery blocks the set may ever have (FFT sizes its cohorts for it).</param>
    internal static (Par3Fft.Layout? Fft, byte[] Generator) ChooseField(Par3Code code, ulong blockCount, int recoveryCount, ulong firstRecoveryBlock, int maxRecovery, int interleave)
    {
        if (code == Par3Code.Fft && recoveryCount > 0)
        {
            int cohorts = interleave > 0 ? interleave + 1 : Par3Fft.ChooseCohorts(blockCount, maxRecovery);
            var layout = Par3Fft.Plan(blockCount, maxRecovery, cohorts);
            return (layout, layout.Field == null ? [] : layout.Field.Bits == 8 ? [0x1D] : [0x2D, 0x00]);
        }

        ulong needed = blockCount + firstRecoveryBlock + (ulong)recoveryCount;
        return (null, needed <= 255 ? [0x1D] : needed <= 65535 ? [0x0B, 0x10] : [0x8D, 0, 0, 0]);
    }

    /// <summary>
    /// What <see cref="Create"/> would use for these numbers, e.g. "FFT Reed-Solomon (GF(2^16), 1 cohort(s))" or
    /// "Cauchy Reed-Solomon (GF(2^8))". Used to preview a set before creating it.
    /// </summary>
    public static string DescribeCode(Par3Code code, ulong blockCount, int recoveryCount, ulong firstRecoveryBlock = 0, int? maxRecoveryBlocks = null, int interleave = 0)
    {
        var resolved = ResolveCode(code, blockCount, recoveryCount);
        int maxRecovery = Math.Max(recoveryCount + (int)firstRecoveryBlock, maxRecoveryBlocks ?? 0);
        var (fft, generator) = ChooseField(resolved, blockCount, recoveryCount, firstRecoveryBlock, maxRecovery, interleave);
        return Describe(fft, generator);
    }

    private static string Describe(Par3Fft.Layout? fft, byte[] generator) => fft != null
        ? $"FFT Reed-Solomon ({(fft.Field == null ? "XOR" : $"GF(2^{fft.Field.Bits})")}, {fft.Cohorts} cohort(s))"
        : $"Cauchy Reed-Solomon (GF(2^{generator.Length * 8}))";

    /// <summary>
    /// The code that <see cref="Par3Code.Auto"/> stands for: Cauchy for small sets (code word up to 256 blocks, which
    /// fits GF(2^8)) and when there are more recovery than input blocks (which the FFT code cannot do), else FFT.
    /// </summary>
    public static Par3Code ResolveCode(Par3Code code, ulong blockCount, int recoveryCount)
        => code != Par3Code.Auto ? code
            : blockCount + (ulong)recoveryCount <= 256 || (ulong)recoveryCount > blockCount ? Par3Code.Cauchy : Par3Code.Fft;

    /// <summary>
    /// Number of input blocks <see cref="Create"/> makes for files of these lengths (without deduplication, which
    /// depends on the contents): every full block, plus one block per tail of 40+ bytes, or with tail packing the
    /// tails packed first-fit (longest first) into shared blocks. Shorter tails are stored inside the PAR3 packets.
    /// </summary>
    /// <remarks>
    /// Block size 100, files of 250, 130 and 70 bytes: full blocks 2 + 1 + 0 = 3; tails 50, 30 (too short), 70.
    /// Packed: 70 opens a block, 50 does not fit next to it and opens another: 3 + 2 = 5 blocks. Without packing:
    /// also 3 + 2 (one per long tail). With tails 60 and 40 they share one block: 60 + 40 = 100.
    /// </remarks>
    public static ulong CountBlocks(IEnumerable<long> fileLengths, ulong blockSize, bool packTails)
    {
        ulong full = 0;
        var tails = new List<ulong>();
        foreach (var length in fileLengths)
        {
            full += (ulong)length / blockSize;
            ulong tail = (ulong)length % blockSize;
            if (tail >= Par3Chunk.InlineLimit)
            {
                tails.Add(tail);
            }
        }

        if (!packTails)
        {
            return full + (ulong)tails.Count;
        }

        var packed = new FirstFitBlocks(blockSize);
        foreach (var tail in tails.OrderByDescending(t => t))
        {
            packed.Place(tail);
        }

        return full + (ulong)packed.Count;
    }

    /// <summary>The blocks one file of <paramref name="length"/> bytes needs: its full blocks, plus one for a tail of 40+ bytes.</summary>
    public static ulong FileBlocks(long length, ulong blockSize)
        => (ulong)length / blockSize + ((ulong)length % blockSize >= Par3Chunk.InlineLimit ? 1UL : 0UL);

    /// <summary>
    /// The smallest block size (a multiple of 8, at least 64) for which the files make at most
    /// <paramref name="targetCount"/> blocks, counted as <see cref="CountBlocks"/> does. When no size gets that low
    /// (without tail packing every file with a tail needs a block of its own), the smallest size with the fewest blocks.
    /// </summary>
    /// <remarks>
    /// <para>Total ÷ target is where to start, but there the count is usually a little above the target: every file
    /// adds a partly filled last block (23 files, target 32,768 → up to 32,791 blocks). So the size grows from there
    /// in doubling steps until the count fits, then bisection finds the smallest size that fits. The count only roughly
    /// falls as the size grows (tails change with the size), so the result is a size that fits, close to the smallest.</para>
    /// <para>The most it can take is one block for everything with packing (block size = total: all tails share it),
    /// or one block per file without (block size = the largest file).</para>
    /// </remarks>
    public static ulong ChooseBlockSize(IReadOnlyList<long> lengths, int targetCount, bool packTails)
    {
        ulong target = (ulong)Math.Max(1, targetCount);
        long total = lengths.Sum();
        static ulong Round(ulong size) => Math.Max(64, (size + 7) / 8 * 8);
        ulong Count(ulong size) => CountBlocks(lengths, size, packTails);

        // The size with the fewest blocks; a target below its count is raised to it.
        ulong most = Round((ulong)Math.Max(1, packTails ? total : lengths.DefaultIfEmpty(0).Max()));
        target = Math.Max(target, Count(most));
        ulong lo = Round((ulong)Math.Max(1, (total + (long)target - 1) / (long)target));
        if (lo >= most)
        {
            return most;
        }

        if (Count(lo) <= target)
        {
            return lo;
        }

        // Invariant: Count(lo) > target, Count(hi) <= target, lo < hi <= most.
        ulong step = 8, hi = Math.Min(most, lo + step);
        while (Count(hi) > target)
        {
            lo = hi;
            step *= 2;
            hi = Math.Min(most, lo + step);
        }

        while (hi - lo > 8)
        {
            ulong mid = (lo + (hi - lo) / 2) / 8 * 8;
            if (Count(mid) <= target)
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

    private static int LeopardLog2(int m) => System.Numerics.BitOperations.Log2((uint)m);

    private static (List<FileInfoEntry> Files, List<string> Directories) Enumerate(Par3CreateOptions options, string baseDir)
    {
        var files = new Dictionary<string, FileInfoEntry>(StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        void AddDirChain(string rel)
        {
            var parts = rel.Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                dirs.Add(string.Join('/', parts.Take(i)));
            }
        }

        foreach (var input in options.Inputs)
        {
            var full = Path.GetFullPath(input);
            if (Directory.Exists(full))
            {
                var rel = PathSafety.ToStoredName(baseDir, full);
                if (rel != ".")
                {
                    dirs.Add(rel);
                    AddDirChain(rel + "/x");
                }

                var option = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var d in Directory.EnumerateDirectories(full, "*", option))
                {
                    var dr = PathSafety.ToStoredName(baseDir, d);
                    dirs.Add(dr);
                    AddDirChain(dr + "/x");
                }

                foreach (var f in Directory.EnumerateFiles(full, "*", option))
                {
                    AddFile(f);
                }
            }
            else if (File.Exists(full))
            {
                AddFile(full);
            }
            else
            {
                throw new FileNotFoundException($"Input '{input}' does not exist.", input);
            }
        }

        void AddFile(string path)
        {
            var full = Path.GetFullPath(path);
            if (string.Equals(full, Path.GetFullPath(options.OutputPath), StringComparison.Ordinal) || full.EndsWith(".par3", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var rel = PathSafety.ToStoredName(baseDir, full);
            AddDirChain(rel);
            files.TryAdd(rel, new FileInfoEntry { FullPath = full, RelativePath = rel, Length = new FileInfo(full).Length });
        }

        return (files.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList(), dirs.OrderBy(d => d, StringComparer.Ordinal).ToList());
    }

    private static void HashFiles(List<FileInfoEntry> files, ulong blockSize, OperationContext context)
    {
        long total = Math.Max(1, files.Sum(f => f.Length));
        long done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Threads), CancellationToken = context.CancellationToken }, f =>
        {
            int full = (int)((ulong)f.Length / blockSize);
            f.BlockChecks = new BlockChecksum[full];
            var hasher = new Blake3Hasher();
            using var fs = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            var buf = new byte[blockSize];
            ulong crc16k = 0;
            long pos = 0;
            for (int b = 0; b <= full; b++)
            {
                int want = b < full ? (int)blockSize : (int)((ulong)f.Length % blockSize);
                if (want == 0)
                {
                    break;
                }

                int n = fs.ReadAtLeast(buf.AsSpan(0, want), want, throwOnEndOfStream: true);
                var data = buf.AsSpan(0, n);
                hasher.Update(data);
                if (pos < 16384)
                {
                    int take = (int)Math.Min(16384 - pos, n);
                    crc16k = Crc64Iso.Compute(data[..take], crc16k);
                }

                if (b < full)
                {
                    f.BlockChecks[b] = new BlockChecksum(Crc64Iso.Compute(data), Fingerprint.Compute(data));
                }
                else if (n < Par3Chunk.InlineLimit)
                {
                    f.InlineTail = data.ToArray();
                }
                else
                {
                    f.TailCrc = Crc64Iso.Compute(data[..Par3Chunk.InlineLimit]);
                    f.TailHash = Fingerprint.Compute(data);
                }

                pos += n;
                context.Observer.Progress((double)Interlocked.Add(ref done, n) / total, "Hashing");
            }

            var h = new byte[16];
            hasher.Finalize(h);
            f.FileHash = Fingerprint.FromBytes(h);
            f.Hash16k = crc16k;
        });
    }

    private static void BuildChunks(FileInfoEntry f, ulong blockSize)
    {
        if (f.Length == 0)
        {
            return;
        }

        ulong tail = (ulong)f.Length % blockSize;
        int i = 0;
        var idx = f.BlockIndices;
        while (i < idx.Length)
        {
            int j = i + 1;
            while (j < idx.Length && idx[j] == idx[j - 1] + 1)
            {
                j++;
            }

            bool last = j == idx.Length;
            f.Chunks.Add(MakeChunk(f, (ulong)(j - i) * blockSize + (last ? tail : 0), idx[i], last ? tail : 0));
            i = j;
        }

        if (idx.Length == 0)
        {
            f.Chunks.Add(MakeChunk(f, tail, 0, tail));
        }
    }

    private static Par3Chunk MakeChunk(FileInfoEntry f, ulong length, ulong firstBlock, ulong tail) => new()
    {
        Length = length,
        FirstBlock = firstBlock,
        InlineTail = tail is > 0 and < Par3Chunk.InlineLimit ? f.InlineTail : null,
        TailCrc = tail >= Par3Chunk.InlineLimit ? f.TailCrc : 0,
        TailHash = tail >= Par3Chunk.InlineLimit ? f.TailHash : default,
        TailBlock = tail >= Par3Chunk.InlineLimit ? f.TailBlock : 0,
        TailOffset = tail >= Par3Chunk.InlineLimit ? f.TailOffset : 0,
    };

    private static List<Fingerprint> ChildrenOf(string dir, List<FileInfoEntry> files, Dictionary<string, Fingerprint> dirChecksums)
    {
        var children = new List<Fingerprint>();
        string prefix = dir.Length == 0 ? string.Empty : dir + "/";
        foreach (var f in files)
        {
            if (f.RelativePath.StartsWith(prefix, StringComparison.Ordinal) && f.RelativePath.IndexOf('/', prefix.Length) < 0)
            {
                children.Add(f.PacketChecksum);
            }
        }

        foreach (var (d, checksum) in dirChecksums)
        {
            if (d.StartsWith(prefix, StringComparison.Ordinal) && d.Length > prefix.Length && d.IndexOf('/', prefix.Length) < 0)
            {
                children.Add(checksum);
            }
        }

        return children;
    }

    private static List<Fingerprint> MakeOptions(InputSetId setId, string path, Par3CreateOptions options, Dictionary<Fingerprint, byte[]> cache, bool isDirectory = false)
    {
        var list = new List<Fingerprint>();
        void Add(byte[] packet)
        {
            var c = Par3PacketWriter.ChecksumOf(packet);
            cache.TryAdd(c, packet);
            list.Add(c);
        }

        if (options.UnixPermissions)
        {
            var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            long mtime = (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
            ushort mode = OperatingSystem.IsWindows() ? (ushort)(isDirectory ? 0x1ED : 0x1A4) : (ushort)((int)File.GetUnixFileMode(path) & 0xFFF);
            Add(Par3PacketWriter.UnixPermissions(setId, Par3UnixPermissionsPacket.UnsetTime, Par3UnixPermissionsPacket.UnsetTime, mtime,
                Par3UnixPermissionsPacket.UnsetId, Par3UnixPermissionsPacket.UnsetId, mode, string.Empty, string.Empty));
        }

        if (options.FatPermissions)
        {
            var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            const long epochDiff = 504911232000000000L; // .NET ticks of 1601-01-01, the FILETIME epoch
            ushort attributes = (ushort)((int)info.Attributes & 0x2027);
            Add(Par3PacketWriter.FatPermissions(setId, (ulong)(info.CreationTimeUtc.Ticks - epochDiff), Par3FatPermissionsPacket.UnsetTime,
                (ulong)(info.LastWriteTimeUtc.Ticks - epochDiff), attributes));
        }

        return list;
    }

    private static ulong ReproducibleUnique(List<FileInfoEntry> files, ulong blockSize, byte[] generator)
    {
        var h = new Blake3Hasher();
        h.Update(BitConverter.GetBytes(blockSize));
        h.Update(generator);
        foreach (var f in files)
        {
            h.Update(System.Text.Encoding.UTF8.GetBytes(f.RelativePath));
            h.Update(f.FileHash.ToArray());
        }

        var b = new byte[8];
        h.Finalize(b);
        return BitConverter.ToUInt64(b);
    }

    internal sealed record Volume(string Path, int First, int Count);

    /// <summary>
    /// The recovery files for <paramref name="recoveryCount"/> blocks from <paramref name="firstRecoveryBlock"/> on:
    /// "set.vol{first}+{count}.par3", both numbers padded to at least 2 digits. Used by <see cref="Create"/>,
    /// <see cref="Par3Extender"/> and, to preview a set before creating it, by <c>ParClient.Plan</c>.
    /// </summary>
    /// <param name="largestFile">Length of the largest input file (for <see cref="RecoveryFileScheme.LimitSize"/>).</param>
    public static IReadOnlyList<RecoveryFilePlan> PlanRecoveryFiles(string outputPath, RecoveryFileScheme scheme, int? volumeCount, ulong firstRecoveryBlock, int recoveryCount, ulong blockSize, long largestFile)
        => PlanVolumes(outputPath, scheme, volumeCount, firstRecoveryBlock, recoveryCount, ".vol", LimitBlocks(largestFile, blockSize))
            .Select(v => new RecoveryFilePlan(v.Path, (long)firstRecoveryBlock + v.First, v.Count)).ToList();

    /// <summary>The most blocks one recovery file may hold under <see cref="RecoveryFileScheme.LimitSize"/>: the largest file's full blocks.</summary>
    internal static long LimitBlocks(long largestFile, ulong blockSize) => Math.Max(1, largestFile / (long)Math.Max(1, blockSize));

    internal static List<Volume> PlanVolumes(string outputPath, RecoveryFileScheme scheme, int? volumeCount, ulong firstRecoveryBlock, int count, string marker, long limitBlocks)
    {
        var counts = RecoveryFileSizing.Counts(count, scheme, volumeCount, limitBlocks);
        var baseName = outputPath.EndsWith(".par3", StringComparison.OrdinalIgnoreCase) ? outputPath[..^5] : outputPath;
        ulong first = firstRecoveryBlock;
        int d1 = Math.Max(2, (first + (ulong)count).ToString(CultureInfo.InvariantCulture).Length);
        int d2 = Math.Max(2, (counts.Count == 0 ? 0 : counts.Max()).ToString(CultureInfo.InvariantCulture).Length);
        var result = new List<Volume>();
        int pos = 0;
        foreach (var c in counts)
        {
            result.Add(new Volume($"{baseName}{marker}{(first + (ulong)pos).ToString(CultureInfo.InvariantCulture).PadLeft(d1, '0')}+{c.ToString(CultureInfo.InvariantCulture).PadLeft(d2, '0')}.par3", pos, c));
            pos += c;
        }

        return result;
    }

    /// <summary>
    /// Copies of the vital packets in every recovery file: <see cref="VitalCopies"/> for a loss rate of recovery ÷ input
    /// blocks (at least 1%), spread over the files, at least one each. Example: 50 vital packets at 100% redundancy need
    /// 26 copies; over 15 files that is 2 copies in every file.
    /// </summary>
    internal static int VitalCopiesPerFile(int vitalPackets, int recoveryCount, ulong blockCount, int files)
    {
        int copies = VitalCopies(vitalPackets, Math.Max(0.01, (double)recoveryCount / Math.Max(1, (double)blockCount)));
        return Math.Max(1, (copies + files - 1) / Math.Max(1, files));
    }

    /// <summary>The data parts of archive mode: equal parts when a file count is given, else 1, 2, 4, ... blocks per part.</summary>
    private static List<Volume> PlanDataParts(string outputPath, Par3CreateOptions options, ulong blockCount)
        => PlanVolumes(outputPath, options.VolumeCount is > 0 ? RecoveryFileScheme.Uniform : RecoveryFileScheme.Exponential, options.VolumeCount, 0, (int)blockCount, ".part", long.MaxValue);

    /// <summary>Copies of each vital packet needed for a 1-in-a-million loss chance (spec Appendix D).</summary>
    internal static int VitalCopies(int vitalPackets, double failureRate)
    {
        double f = Math.Clamp(failureRate, 0.01, 0.5);
        double n = (Math.Log(1e-6) - Math.Log(Math.Max(1, vitalPackets))) / Math.Log(f);
        return Math.Max(1, (int)Math.Ceiling(n));
    }

    internal static void WriteRecovery(InputSetId setId, Fingerprint rootChecksum, Fingerprint matrixChecksum, byte[] generator, Par3Fft.Layout? fft, ulong blockCount,
        List<ulong> recoveryIndices, List<Volume> volumes, Par3BlockSource blocks, long blockSize, List<byte[]> vital, byte[] creator, Par3CreateOptions options, OperationContext context, BlockStateMap map)
    {
        int recoveryCount = recoveryIndices.Count;
        var streams = volumes.Select(v => new FileStream(v.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess)).ToList();
        try
        {
            var outputs = new (FileStream Stream, long DataOffset, byte[] Header)[recoveryCount];
            for (int v = 0; v < volumes.Count; v++)
            {
                long pos = 0;
                for (int k = 0; k < volumes[v].Count; k++)
                {
                    int j = volumes[v].First + k;
                    var header = Par3PacketWriter.RecoveryDataHeader(setId, rootChecksum, matrixChecksum, recoveryIndices[j], blockSize);
                    outputs[j] = (streams[v], pos + header.Length, header);
                    map.RecoveryContainer[j] = Path.GetFileName(volumes[v].Path);
                    pos += header.Length + blockSize;
                }

                streams[v].SetLength(pos);
            }

            var done = new bool[recoveryCount];
            var sink = new RecoverySink(outputs, blockSize, done, map);
            var progress = context.Observer.ToProgress();
            var combine = context.Combine.Create(progress);
            var source = new Par3BlockSource(blocks.Blocks, blocks.Handles) { OnRead = b => map.Set(b, BlockState.Processed) };
            if (fft != null)
            {
                Par3Fft.Encode(fft, blockCount, recoveryIndices, source, sink, blockSize, combine);
            }
            else
            {
                Par3Fields.With(generator, new CauchyEncode(blockCount, recoveryIndices, source, sink, blockSize, combine, options.CauchyConvention));
            }

            // Then the packet headers (with the checksums), and the vital packets after the recovery data, repeated
            // per spec Appendix D. Progress: the headers count as one copy of the vital packets.
            context.Observer.Stage(Par3Stages.WritingRecovery);
            int perFile = VitalCopiesPerFile(vital.Count, recoveryCount, blockCount, volumes.Count);
            double steps = 1 + (double)perFile * streams.Count;
            for (int j = 0; j < recoveryCount; j++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!done[j])
                {
                    FinishChecksumFromDisk(outputs[j].Stream, outputs[j].DataOffset, outputs[j].Header, blockSize);
                }

                RandomAccess.Write(outputs[j].Stream.SafeFileHandle, outputs[j].Header, outputs[j].DataOffset - outputs[j].Header.Length);
                map.SetRecovery(j, RecoveryBlockState.Created);
                if (j % 256 == 255)
                {
                    context.Observer.Progress((j + 1.0) / recoveryCount / steps);
                }
            }

            int copiesWritten = 0;
            foreach (var s in streams)
            {
                s.Seek(0, SeekOrigin.End);
                for (int c = 0; c < perFile; c++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    foreach (var p in vital)
                    {
                        s.Write(p);
                    }

                    context.Observer.Progress((1 + ++copiesWritten) / steps);
                }

                s.Write(creator);
            }
        }
        finally
        {
            foreach (var s in streams)
            {
                s.Dispose();
            }
        }
    }

    private static List<string> WriteDataPackets(InputSetId setId, string outputPath, ulong blockCount, Par3BlockSource blocks, ulong blockSize, List<byte[]> vital, byte[] creator, Par3CreateOptions options, IParOperationObserver observer)
    {
        observer.Stage(Par3Stages.StoringData);
        var parts = PlanDataParts(outputPath, options, blockCount);
        var buf = new byte[blockSize];
        ulong stored = 0;
        foreach (var part in parts)
        {
            using var fs = new FileStream(part.Path, FileMode.Create, FileAccess.Write);
            for (int i = 0; i < part.Count; i++)
            {
                if (++stored % 256 == 0)
                {
                    observer.Progress((double)stored / blockCount);
                }

                ulong idx = (ulong)(part.First + i);
                blocks.Read((int)idx, 0, buf);
                int len = buf.Length;
                var header = Par3PacketWriter.DataHeader(setId, idx, len);
                var hasher = new Blake3Hasher();
                hasher.Update(header.AsSpan(24));
                hasher.Update(buf.AsSpan(0, len));
                hasher.Finalize(header.AsSpan(8, 16));
                fs.Write(header);
                fs.Write(buf, 0, len);
            }

            foreach (var p in vital)
            {
                fs.Write(p);
            }

            fs.Write(creator);
        }

        return parts.Select(p => p.Path).ToList();
    }

    internal static void FinishChecksumFromDisk(FileStream stream, long dataOffset, byte[] header, long dataLength)
    {
        var hasher = new Blake3Hasher();
        hasher.Update(header.AsSpan(24));
        var buf = new byte[(int)Math.Min(dataLength, 1 << 20)];
        long left = dataLength, off = dataOffset;
        while (left > 0)
        {
            int n = (int)Math.Min(left, buf.Length);
            FileHandleCache.ReadPadded(stream.SafeFileHandle, off, buf.AsSpan(0, n));
            hasher.Update(buf.AsSpan(0, n));
            off += n;
            left -= n;
        }

        hasher.Finalize(header.AsSpan(8, 16));
    }

    internal sealed class CauchyEncode(ulong blockCount, List<ulong> recoveryIndices, IRegionSource source, IRegionSink sink, long blockSize, LinearCombineOptions options, Par3CauchyConvention convention) : IFieldAction<bool>
    {
        public bool Run<T>(IGaloisField<T> field) where T : IEquatable<T>
        {
            LinearCombiner.Combine(field, new CauchyCoefficients<T>(field, recoveryIndices, convention), (int)blockCount, recoveryIndices.Count, source, sink, blockSize, options);
            return true;
        }
    }

    internal sealed class CauchyCoefficients<T>(IGaloisField<T> field, IReadOnlyList<ulong> recoveryIndices, Par3CauchyConvention convention) : ICoefficientProvider<T> where T : IEquatable<T>
    {
        public void Fill(int output, int firstInput, Span<T> destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = Par3Cauchy.Coefficient(field, recoveryIndices[output], (ulong)(firstInput + i), convention);
            }
        }
    }

    internal sealed class RecoverySink((FileStream Stream, long DataOffset, byte[] Header)[] outputs, long blockSize, bool[] done, BlockStateMap map) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source)
        {
            var (stream, dataOffset, header) = outputs[index];
            RandomAccess.Write(stream.SafeFileHandle, source, dataOffset + offset);
            if (offset == 0 && source.Length == blockSize)
            {
                var hasher = new Blake3Hasher();
                hasher.Update(header.AsSpan(24));
                hasher.Update(source);
                hasher.Finalize(header.AsSpan(8, 16));
                done[index] = true;
            }

            map.SetRecovery(index, RecoveryBlockState.Created);
        }
    }
}

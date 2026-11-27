using System.Security.Cryptography;
using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Core.Util;
using DeveParTools.Par2.Packets;

namespace DeveParTools.Par2;

/// <summary>How recovery slices are distributed over the .volXX+YY.par2 files.</summary>
public enum Par2VolumeScheme
{
    /// <summary>1, 2, 4, 8, ... slices per file (par2cmdline default).</summary>
    Exponential,

    /// <summary>The same number of slices in every file (see <see cref="Par2CreateOptions.VolumeCount"/>).</summary>
    Uniform,

    /// <summary>Exponential, but no file holds more data than the largest input file.</summary>
    LimitSize,
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

    public Par2VolumeScheme Scheme { get; init; } = Par2VolumeScheme.Exponential;

    /// <summary>Number of volume files for <see cref="Par2VolumeScheme.Uniform"/>.</summary>
    public int? VolumeCount { get; init; }

    public string? Comment { get; init; }

    public string Creator { get; init; } = Par2Creator.DefaultCreator;
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
        var token = context.CancellationToken;
        var outputPath = Path.GetFullPath(options.OutputPath);
        var baseDir = Path.GetFullPath(options.BaseDirectory ?? Path.GetDirectoryName(outputPath)!);

        // 1. Inputs: names, lengths, MD5 of the first 16 KiB, File IDs.
        observer.Stage("Preparing");
        var inputs = options.InputFiles
            .Select(p => Path.GetFullPath(p))
            .Distinct(StringComparer.Ordinal)
            .Select(p => new InputFile { Path = p, Name = PathSafety.ToStoredName(baseDir, p), Length = new FileInfo(p).Length })
            .ToList();
        if (inputs.Count == 0)
        {
            throw new ArgumentException("No input files.");
        }

        if (inputs.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
        {
            throw new ArgumentException("Input file names must be unique.");
        }

        long sliceSize = options.SliceSize ?? ChooseSliceSize(inputs.Select(i => i.Length).ToList(), options.SliceCount);
        if (sliceSize <= 0 || sliceSize % 4 != 0)
        {
            throw new ArgumentException("Slice size must be a positive multiple of 4.");
        }

        var buf16 = new byte[16384];
        foreach (var f in inputs)
        {
            using var fs = File.OpenRead(f.Path);
            int n = fs.ReadAtLeast(buf16, buf16.Length, throwOnEndOfStream: false);
            f.Hash16k = Md5Hash.Compute(buf16.AsSpan(0, n));
            f.FileId = Par2PacketWriter.ComputeFileId(f.Hash16k, f.Length, f.Name);
        }

        // 2. Slice numbering: files in File ID order, slices in file order.
        inputs.Sort((a, b) => a.FileId.CompareTo(b.FileId));
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

        int recoveryCount = totalSlices == 0 ? 0 : options.RecoverySliceCount ?? (int)Math.Ceiling(totalSlices * options.RedundancyPercent / 100.0);
        if (recoveryCount < 0 || options.FirstExponent < 0 || (long)options.FirstExponent + recoveryCount > 65535)
        {
            throw new ArgumentException("Recovery slice exponents must lie in [0, 65535).");
        }

        var fileIds = inputs.Select(i => i.FileId).ToList();
        var mainBody = Par2PacketWriter.MainBody(sliceSize, fileIds, []);
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
        var volumes = PlanVolumes(outputPath, options, recoveryCount, sliceSize, inputs.Max(i => i.Length));
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
        var combineOptions = context.Combine.Create(new Progress<double>(p => observer.Progress(p, "Encoding")));
        int sliceLen = LinearCombiner.PlanSliceLength(2, totalSlices, recoveryCount, sliceSize, combineOptions);
        bool singlePass = sliceLen >= sliceSize;

        using var handles = new FileHandleCache();
        if (!singlePass || recoveryCount == 0)
        {
            observer.Stage("Hashing");
            HashInputs(inputs, sliceSize, sliceHashes, sliceCrc, handles, context, map);
        }

        var recoveryHeaders = new List<(string File, long Offset, byte[] Header)>();
        var recoveryDone = new bool[recoveryCount];
        if (recoveryCount > 0)
        {
            observer.Stage("Encoding");
            var volumeStreams = volumes.ToDictionary(v => v, v =>
                new FileStream(v.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess));
            try
            {
                var outputs = new List<(SafeHandleWrapper Handle, long DataOffset, byte[] Header)>();
                foreach (var v in volumes)
                {
                    long pos = 0;
                    for (int k = 0; k < v.Count; k++)
                    {
                        var header = Par2PacketWriter.RecoverySliceHeader(setId, (uint)(v.FirstExponent + k), sliceSize);
                        recoveryHeaders.Add((v.Path, pos, header));
                        outputs.Add((new SafeHandleWrapper(volumeStreams[v]), pos + header.Length, header));
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

                // Recovery packets whose MD5 could not be computed on the fly (multi-pass): read back.
                for (int j = 0; j < recoveryCount; j++)
                {
                    if (!recoveryDone[j])
                    {
                        FinishRecoveryPacketFromDisk(outputs[j].Handle.Stream, outputs[j].DataOffset, outputs[j].Header, sliceSize);
                    }

                    RandomAccess.Write(outputs[j].Handle.Stream.SafeFileHandle, outputs[j].Header, outputs[j].DataOffset - outputs[j].Header.Length);
                    map.SetRecovery(j, RecoveryBlockState.Created);
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
        observer.Stage("Writing");
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

        if (!string.IsNullOrEmpty(options.Comment))
        {
            critical.Add(Par2PacketWriter.AsciiComment(setId, options.Comment));
        }

        var creator = Par2PacketWriter.Creator(setId, options.Creator);
        using (var index = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            foreach (var p in critical)
            {
                index.Write(p);
            }

            index.Write(creator);
        }

        foreach (var v in volumes)
        {
            using var fs = new FileStream(v.Path, FileMode.Open, FileAccess.Write);
            fs.Seek(0, SeekOrigin.End);
            // More recovery slices in a file → more copies of the critical packets (log2, at least one).
            int copies = 1 + (int)Math.Log2(Math.Max(1, v.Count));
            for (int c = 0; c < copies; c++)
            {
                foreach (var p in critical)
                {
                    fs.Write(p);
                }
            }

            fs.Write(creator);
        }

        observer.Progress(1);
        return new Par2CreateResult
        {
            SetId = setId,
            SliceSize = sliceSize,
            InputSlices = totalSlices,
            RecoverySlices = recoveryCount,
            WrittenFiles = [outputPath, .. volumes.Select(v => v.Path)],
        };
    }

    /// <summary>
    /// Smallest slice size (multiple of 4) for which the slice count (each file rounded up separately) does not
    /// exceed <paramref name="targetCount"/> (and 32768).
    /// </summary>
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
        long hi = Math.Max(lo, (lengths.Max() + 3) / 4 * 4);
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

    private static List<Volume> PlanVolumes(string outputPath, Par2CreateOptions options, int recoveryCount, long sliceSize, long largestFile)
    {
        var counts = new List<int>();
        int remaining = recoveryCount;
        switch (options.Scheme)
        {
            case Par2VolumeScheme.Uniform:
            {
                int n = Math.Clamp(options.VolumeCount ?? 1, 1, Math.Max(1, recoveryCount));
                for (int i = 0; i < n && remaining > 0; i++)
                {
                    int c = (remaining + (n - i) - 1) / (n - i);
                    counts.Add(c);
                    remaining -= c;
                }

                break;
            }

            default:
            {
                int limit = options.Scheme == Par2VolumeScheme.LimitSize ? (int)Math.Max(1, Math.Min(int.MaxValue, largestFile / sliceSize)) : int.MaxValue;
                int next = 1;
                while (remaining > 0)
                {
                    int c = Math.Min(Math.Min(next, remaining), limit);
                    counts.Add(c);
                    remaining -= c;
                    next = (int)Math.Min(int.MaxValue / 2, (long)next * 2);
                }

                break;
            }
        }

        var baseName = outputPath.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) ? outputPath[..^5] : outputPath;
        int lowDigits = Digits(options.FirstExponent + recoveryCount);
        int countDigits = Digits(counts.Count == 0 ? 0 : counts.Max());
        var volumes = new List<Volume>();
        int exp = options.FirstExponent;
        foreach (var c in counts)
        {
            var name = $"{baseName}.vol{exp.ToString().PadLeft(lowDigits, '0')}+{c.ToString().PadLeft(countDigits, '0')}.par2";
            volumes.Add(new Volume(name, exp, c));
            exp += c;
        }

        return volumes;
    }

    private static int Digits(int v) => Math.Max(1, v.ToString().Length);

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

    /// <summary>Wraps a FileStream so the sink can write positionally to its handle.</summary>
    private sealed class SafeHandleWrapper(FileStream stream)
    {
        public FileStream Stream { get; } = stream;
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
    private sealed class RecoverySink(List<(SafeHandleWrapper Handle, long DataOffset, byte[] Header)> outputs, long sliceSize, bool[] done, BlockStateMap map) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source)
        {
            var (h, dataOffset, header) = outputs[index];
            RandomAccess.Write(h.Stream.SafeFileHandle, source, dataOffset + offset);
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

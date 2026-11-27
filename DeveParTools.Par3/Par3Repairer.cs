using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Core.Util;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

public sealed class Par3RepairOptions
{
    /// <summary>Delete damaged originals instead of keeping "name.1" backups.</summary>
    public bool PurgeBackups { get; init; }

    /// <summary>Apply UNIX/FAT permission packets (mtime, mode, attributes) to repaired files.</summary>
    public bool RestorePermissions { get; init; }

    /// <summary>Create links from Link packets (only links that stay inside the base directory).</summary>
    public bool RestoreLinks { get; init; }
}

public sealed class Par3RepairResult
{
    public bool Success { get; init; }

    public IReadOnlyList<string> RepairedFiles { get; init; } = [];

    public IReadOnlyList<string> FailedFiles { get; init; } = [];

    public int BlocksReconstructed { get; init; }

    public string? Message { get; init; }
}

/// <summary>
/// Rebuilds the damaged or missing files of a verified PAR3 input set.
/// </summary>
/// <remarks>
/// <para>Missing input blocks are reconstructed first, into a temporary block file:</para>
/// <list type="bullet">
/// <item><b>FFT sets</b> (par3cmdline "PAR FFT" packet): Leopard decoding per cohort, O(n log n).</item>
/// <item><b>Matrix sets</b> (Cauchy, Sparse Random, Explicit, possibly mixed and from parent sets): every available
/// recovery block is one equation <c>R_r = Σ_i C[r,i]·D_i</c>. L independent equations are selected for the L
/// missing blocks (any L for Cauchy), the L×L system is inverted and solved in two streaming passes, exactly like
/// PAR2 (see <see cref="GfMatrix{T}.TryInvert"/> for a worked inversion example).</item>
/// </list>
/// <para>Then every damaged file is written to a temporary file from its chunks: whole blocks and packed tails come
/// from wherever they were found (or from the rebuilt blocks), inline tails from the File packet, unprotected
/// chunks are kept from the old file when possible. The result is checked against the file's BLAKE3 hash before it
/// replaces the original.</para>
/// </remarks>
public static class Par3Repairer
{
    public static Par3RepairResult Repair(Par3VerifyResult verify, Par3RepairOptions? options = null, OperationContext? context = null)
    {
        options ??= new Par3RepairOptions();
        context ??= OperationContext.Default;
        var observer = context.Observer;
        var set = verify.Set;
        var map = context.Blocks;
        long bs = (long)set.BlockSize;

        if (verify.AllFilesComplete)
        {
            return new Par3RepairResult { Success = true, Message = "All files are already correct." };
        }

        var missing = Enumerable.Range(0, verify.Blocks.Length).Where(i => verify.Blocks[i] == null).ToList();
        if (!verify.RepairPossible)
        {
            return new Par3RepairResult { Success = false, Message = $"Repair not possible: {missing.Count} block(s) missing but only {verify.RecoveryBlocksAvailable} recovery block(s) available." };
        }

        observer.Stage("Repairing");
        using var handles = new FileHandleCache();
        string? blockFile = null;
        var rebuilt = new Dictionary<int, long>(); // block → offset in the temporary block file
        try
        {
            if (missing.Count > 0)
            {
                foreach (var m in missing)
                {
                    map?.Set(m, BlockState.Repairing);
                }

                blockFile = Path.Combine(verify.BaseDirectory, $".deveparttools-blocks-{Guid.NewGuid():N}.tmp");
                using (var fs = new FileStream(blockFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess))
                {
                    fs.SetLength(missing.Count * bs);
                    for (int i = 0; i < missing.Count; i++)
                    {
                        rebuilt[missing[i]] = i * bs;
                    }

                    var sink = new BlockFileSink(fs, rebuilt);
                    // The Cauchy x_I convention differs between the spec text and par3cmdline; the block checksums tell
                    // which one the creator used.
                    bool ok = false;
                    foreach (var convention in new[] { Par3CauchyConvention.Par3cmdline, Par3CauchyConvention.Specification })
                    {
                        bool usedMatrix = ReconstructBlocks(verify, missing, sink, handles, context, convention);
                        fs.Flush(true);
                        ok = RebuiltBlocksValid(verify, missing, fs, rebuilt);
                        if (ok || !usedMatrix)
                        {
                            break;
                        }
                    }

                    if (!ok)
                    {
                        observer.Log(ParLogLevel.Warning, "Rebuilt blocks do not match their checksums.");
                    }
                }
            }

            // Rebuild files.
            var toRebuild = verify.Files.Where(f => f.Status != Par3FileStatus.Complete && f.TargetPath.Length > 0).ToList();
            var repaired = new List<string>();
            var failed = new List<string>();
            foreach (var fv in toRebuild)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var tmp = fv.TargetPath + ".deveparttools.tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(fv.TargetPath)!);
                WriteFile(fv, verify, tmp, blockFile, rebuilt, handles);
                if (HashFile(tmp, fv.File) != fv.File.Packet.FileHash)
                {
                    failed.Add(fv.TargetPath);
                    File.Delete(tmp);
                    continue;
                }

                handles.Close(fv.TargetPath);
                if (File.Exists(fv.TargetPath))
                {
                    if (options.PurgeBackups)
                    {
                        File.Delete(fv.TargetPath);
                    }
                    else
                    {
                        File.Move(fv.TargetPath, NextBackupName(fv.TargetPath));
                    }
                }

                File.Move(tmp, fv.TargetPath);
                fv.Status = Par3FileStatus.Complete;
                repaired.Add(fv.TargetPath);
            }

            foreach (var d in verify.MissingDirectories)
            {
                Directory.CreateDirectory(d);
            }

            if (options.RestorePermissions)
            {
                Par3Metadata.Apply(verify, observer);
            }

            if (options.RestoreLinks)
            {
                Par3Metadata.CreateLinks(verify, observer);
            }

            foreach (var m in missing)
            {
                map?.Set(m, BlockState.Repaired);
            }

            return new Par3RepairResult
            {
                Success = failed.Count == 0,
                RepairedFiles = repaired,
                FailedFiles = failed,
                BlocksReconstructed = missing.Count,
                Message = failed.Count == 0 ? $"Repaired {repaired.Count} file(s)." : $"{failed.Count} file(s) failed verification after repair.",
            };
        }
        finally
        {
            if (blockFile != null)
            {
                handles.Close(blockFile);
                File.Delete(blockFile);
            }
        }
    }

    public static string NextBackupName(string path)
    {
        for (int i = 1; ; i++)
        {
            var candidate = $"{path}.{i}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Checks every rebuilt block against the CRC-64 and BLAKE3 from the External Data packets.</summary>
    private static bool RebuiltBlocksValid(Par3VerifyResult verify, List<int> missing, FileStream fs, Dictionary<int, long> positions)
    {
        var buf = new byte[verify.Set.BlockSize];
        foreach (int b in missing)
        {
            if (verify.Set.BlockChecksums[b] is not { } expected)
            {
                continue;
            }

            FileHandleCache.ReadPadded(fs.SafeFileHandle, positions[b], buf);
            if (Crc64Iso.Compute(buf) != expected.Crc64 || Fingerprint.Compute(buf) != expected.Hash)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rebuilds the missing blocks; returns true when the matrix (not FFT) path was used.</summary>
    private static bool ReconstructBlocks(Par3VerifyResult verify, List<int> missing, BlockFileSink sink, FileHandleCache handles, OperationContext context, Par3CauchyConvention convention)
    {
        var set = verify.Set;
        long bs = (long)set.BlockSize;
        var available = verify.Blocks.Select(b => b != null).ToArray();
        var known = new Par3BlockSource(verify.Blocks.Select(b => b?.Pieces ?? (IReadOnlyList<(ulong, ulong, PieceSource)>)[]).ToArray(), handles);
        var options = context.Combine.Create(new Progress<double>(p => context.Observer.Progress(p, "Reconstructing")), context.MemoryBudget / 3);

        var fft = set.RecoveryBlocks.Where(r => r.Matrix is Par3FftMatrixPacket).ToList();
        var matrixBlocks = set.RecoveryBlocks.Where(r => r.Matrix is not Par3FftMatrixPacket).ToList();
        if (fft.Count >= missing.Count && fft.Count > 0)
        {
            var packet = (Par3FftMatrixPacket)fft[0].Matrix;
            int cohorts = (int)packet.Interleave + 1;
            int m = 1 << packet.MaxRecoveryLog2;
            var layout = set.Start.GfSize == 0
                ? new Par3Fft.Layout(1, (int)set.BlockCount, 1, null)
                : new Par3Fft.Layout(cohorts, (int)((set.BlockCount + (ulong)cohorts - 1) / (ulong)cohorts), m,
                    set.Start.GfSize == 1 ? Core.Codes.Fft.LeopardField.FF8 : Core.Codes.Fft.LeopardField.FF16);
            var recoveryMap = new Dictionary<ulong, int>();
            for (int i = 0; i < fft.Count; i++)
            {
                recoveryMap.TryAdd(fft[i].Index, i);
            }

            var reader = new RecoveryReader(fft, handles);
            Par3Fft.Decode(layout, set.BlockCount, available, recoveryMap, known, reader, sink, bs, options);
            return false;
        }

        Par3Fields.With(set.Start.GeneratorBytes, new MatrixRepair(verify, missing, matrixBlocks, known, sink, handles, options, convention));
        return true;
    }

    /// <summary>Generic linear-algebra repair for Cauchy / Sparse / Explicit matrices.</summary>
    private sealed class MatrixRepair(Par3VerifyResult verify, List<int> missing, List<Par3RecoveryBlock> recovery, Par3BlockSource known, IRegionSink sink, FileHandleCache handles, LinearCombineOptions options, Par3CauchyConvention convention) : IFieldAction<bool>
    {
        public bool Run<T>(IGaloisField<T> field) where T : IEquatable<T>
        {
            var set = verify.Set;
            int l = missing.Count;
            var coefficients = new Par3Coefficients<T>(field, set, convention);

            // Choose L recovery rows that are independent on the missing columns (greedy elimination).
            var chosen = new List<Par3RecoveryBlock>();
            var basis = new List<T[]>();
            var pivots = new List<int>();
            foreach (var r in recovery)
            {
                var row = new T[l];
                for (int c = 0; c < l; c++)
                {
                    row[c] = coefficients.Get(r, (ulong)missing[c]);
                }

                for (int k = 0; k < basis.Count; k++)
                {
                    var f = row[pivots[k]];
                    if (!field.IsZero(f))
                    {
                        field.RowMulAdd(row, basis[k], f);
                    }
                }

                int pivot = Array.FindIndex(row, v => !field.IsZero(v));
                if (pivot < 0)
                {
                    continue;
                }

                field.RowScale(row, field.Inv(row[pivot]));
                basis.Add(row);
                pivots.Add(pivot);
                chosen.Add(r);
                if (chosen.Count == l)
                {
                    break;
                }
            }

            if (chosen.Count < l)
            {
                throw new InvalidOperationException("The available recovery blocks are not independent enough to rebuild the missing blocks.");
            }

            var a = new GfMatrix<T>(l, l, field);
            for (int r = 0; r < l; r++)
            {
                for (int c = 0; c < l; c++)
                {
                    a[r, c] = coefficients.Get(chosen[r], (ulong)missing[c]);
                }
            }

            if (!a.TryInvert(options.CancellationToken))
            {
                throw new InvalidOperationException("Recovery matrix is singular.");
            }

            var missingSet = new HashSet<int>(missing);
            var knownBlocks = Enumerable.Range(0, (int)set.BlockCount).Where(i => !missingSet.Contains(i)).ToList();
            var source = new PassOneSource(known, knownBlocks, new RecoveryReader(chosen, handles));
            var pass1 = new PassOneCoefficients<T>(field, coefficients, knownBlocks, chosen);
            var pass2 = new SolveSink<T>(field, a, missing, sink, l, options);
            LinearCombiner.Combine(field, pass1, knownBlocks.Count + l, l, source, pass2, (long)set.BlockSize, options);
            return true;
        }
    }

    /// <summary>Coefficient of input block i in a recovery block, for any matrix packet type.</summary>
    internal sealed class Par3Coefficients<T>(IGaloisField<T> field, Par3InputSet set, Par3CauchyConvention convention) where T : IEquatable<T>
    {
        private readonly Dictionary<Fingerprint, List<T[]>> _sparse = new();
        private readonly Dictionary<Fingerprint, Dictionary<ulong, T>> _explicit = new();

        public T Get(Par3RecoveryBlock r, ulong input)
        {
            if (input >= r.CoveredBlocks)
            {
                return field.Zero;
            }

            switch (r.Matrix)
            {
                case Par3CauchyMatrixPacket c:
                    return Par3Cauchy.InRange(c.First, c.LastPlus1, input) ? Par3Cauchy.Coefficient(field, r.Index, input, convention) : field.Zero;
                case Par3SparseMatrixPacket s:
                {
                    if (!Par3Cauchy.InRange(s.First, s.LastPlus1, input) || r.Index >= s.MaxRecovery)
                    {
                        return field.Zero;
                    }

                    if (!_sparse.TryGetValue(s.Checksum, out var rows))
                    {
                        _sparse[s.Checksum] = rows = Par3Sparse.Generate(field, s, set.BlockCount);
                    }

                    ulong row = input - s.First;
                    return row < (ulong)rows.Count ? rows[(int)row][(int)r.Index] : field.Zero;
                }

                case Par3ExplicitMatrixPacket e:
                {
                    if (!_explicit.TryGetValue(e.Checksum, out var entries))
                    {
                        entries = new Dictionary<ulong, T>();
                        int size = field.SymbolSize;
                        for (int p = 0; p + 8 + size <= e.RawBody.Length; p += 8 + size)
                        {
                            entries[BitConverter.ToUInt64(e.RawBody, p)] = field.Read(e.RawBody.AsSpan(p + 8, size));
                        }

                        _explicit[e.Checksum] = entries;
                    }

                    return r.Index == 0 && entries.TryGetValue(input, out var v) ? v : field.Zero;
                }

                default:
                    return field.Zero;
            }
        }
    }

    private sealed class PassOneCoefficients<T>(IGaloisField<T> field, Par3Coefficients<T> coefficients, List<int> knownBlocks, List<Par3RecoveryBlock> chosen) : ICoefficientProvider<T> where T : IEquatable<T>
    {
        public void Fill(int output, int firstInput, Span<T> destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                int input = firstInput + i;
                destination[i] = input < knownBlocks.Count
                    ? coefficients.Get(chosen[output], (ulong)knownBlocks[input])
                    : (input - knownBlocks.Count == output ? field.One : field.Zero);
            }
        }
    }

    private sealed class PassOneSource(Par3BlockSource known, List<int> knownBlocks, RecoveryReader recovery) : IRegionSource
    {
        public void Read(int index, long offset, Span<byte> destination)
        {
            if (index < knownBlocks.Count)
            {
                known.Read(knownBlocks[index], offset, destination);
            }
            else
            {
                recovery.Read(index - knownBlocks.Count, offset, destination);
            }
        }
    }

    /// <summary>Reads recovery block data from the .par3 files (zero-padded to the block size).</summary>
    internal sealed class RecoveryReader(List<Par3RecoveryBlock> blocks, FileHandleCache handles) : IRegionSource
    {
        public void Read(int index, long offset, Span<byte> destination)
        {
            var d = blocks[index].Data;
            long available = Math.Clamp(d.DataLength - offset, 0, destination.Length);
            destination.Clear();
            handles.ReadPadded(d.SourceFile!, d.DataOffset + offset, destination[..(int)available]);
        }
    }

    /// <summary>Collects S for one slice range, then writes D = A⁻¹·S into the block file.</summary>
    private sealed class SolveSink<T>(IGaloisField<T> field, GfMatrix<T> inverse, List<int> missing, IRegionSink output, int l, LinearCombineOptions options) : IRegionSink where T : IEquatable<T>
    {
        private byte[][]? _s;

        public void Write(int index, long offset, ReadOnlySpan<byte> source)
        {
            _s ??= new byte[l][];
            if (_s[index] == null || _s[index].Length != source.Length)
            {
                _s[index] = new byte[source.Length];
            }

            source.CopyTo(_s[index]);
            if (index != l - 1)
            {
                return;
            }

            LinearCombiner.Combine(field, new MatrixCoefficients<T>(inverse), l, l, new MemoryRegions(_s), new OffsetSink(output, missing, offset), source.Length,
                new LinearCombineOptions { Threads = options.Threads, CancellationToken = options.CancellationToken, MemoryBudget = options.MemoryBudget });
        }
    }

    private sealed class OffsetSink(IRegionSink inner, List<int> missing, long rangeOffset) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source) => inner.Write(missing[index], rangeOffset + offset, source);
    }

    /// <summary>Stores rebuilt blocks in the temporary block file.</summary>
    private sealed class BlockFileSink(FileStream stream, Dictionary<int, long> positions) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source) => RandomAccess.Write(stream.SafeFileHandle, source, positions[index] + offset);
    }

    private static void WriteFile(Par3FileVerification fv, Par3VerifyResult verify, string tmp, string? blockFile, Dictionary<int, long> rebuilt, FileHandleCache handles)
    {
        var set = verify.Set;
        ulong bs = set.BlockSize;
        var f = fv.File;
        using var outStream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        outStream.SetLength((long)f.Length);
        var buf = new byte[(int)Math.Min(bs, 1 << 24)];
        bool oldExists = File.Exists(fv.TargetPath);
        ulong offset = 0;
        foreach (var c in f.Chunks)
        {
            if (!c.Protected)
            {
                // Not protected: keep whatever the existing file has at this position (zeros otherwise).
                if (oldExists)
                {
                    for (ulong done = 0; done < c.Length;)
                    {
                        int n = (int)Math.Min(c.Length - done, (ulong)buf.Length);
                        handles.ReadPadded(fv.TargetPath, (long)(offset + done), buf.AsSpan(0, n));
                        RandomAccess.Write(outStream.SafeFileHandle, buf.AsSpan(0, n), (long)(offset + done));
                        done += (ulong)n;
                    }
                }

                offset += c.Length;
                continue;
            }

            ulong full = c.FullBlocks(bs);
            for (ulong b = 0; b < full; b++)
            {
                CopyFromBlock((int)(c.FirstBlock + b), 0, bs, (long)(offset + b * bs));
            }

            ulong tail = c.TailLength(bs);
            if (tail > 0)
            {
                long dst = (long)(offset + full * bs);
                if (tail < Par3Chunk.InlineLimit)
                {
                    RandomAccess.Write(outStream.SafeFileHandle, c.InlineTail, dst);
                }
                else if (verify.Tails.TryGetValue((f, c), out var src))
                {
                    var data = new byte[tail];
                    handles.ReadPadded(src.Path, src.Offset, data);
                    RandomAccess.Write(outStream.SafeFileHandle, data, dst);
                }
                else
                {
                    CopyFromBlock((int)c.TailBlock, (long)c.TailOffset, tail, dst);
                }
            }

            offset += c.Length;
        }

        void CopyFromBlock(int block, long blockOffset, ulong length, long dst)
        {
            for (ulong done = 0; done < length;)
            {
                int n = (int)Math.Min(length - done, (ulong)buf.Length);
                var span = buf.AsSpan(0, n);
                if (rebuilt.TryGetValue(block, out var pos))
                {
                    handles.ReadPadded(blockFile!, pos + blockOffset + (long)done, span);
                }
                else
                {
                    span.Clear();
                    var avail = verify.Blocks[block]!;
                    new Par3BlockSource([avail.Pieces], handles).ReadBlock(0, blockOffset + (long)done, span);
                }

                RandomAccess.Write(outStream.SafeFileHandle, span, dst + (long)done);
                done += (ulong)n;
            }
        }
    }

    private static Fingerprint HashFile(string path, Par3FileEntry file)
    {
        var hasher = new Blake3Hasher();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[1 << 20];
        ulong offset = 0;
        foreach (var c in file.Chunks)
        {
            for (ulong done = 0; done < c.Length;)
            {
                int n = (int)Math.Min(c.Length - done, (ulong)buf.Length);
                fs.ReadExactly(buf, 0, n);
                if (!c.Protected)
                {
                    Array.Clear(buf, 0, n);
                }

                hasher.Update(buf.AsSpan(0, n));
                done += (ulong)n;
            }

            offset += c.Length;
        }

        var h = new byte[16];
        hasher.Finalize(h);
        return Fingerprint.FromBytes(h);
    }
}

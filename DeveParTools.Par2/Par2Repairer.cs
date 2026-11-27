using System.Security.Cryptography;
using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Par2.Packets;

namespace DeveParTools.Par2;

public sealed class Par2RepairOptions
{
    /// <summary>Delete the damaged originals instead of keeping them as "name.1" backups.</summary>
    public bool PurgeBackups { get; init; }
}

public sealed class Par2RepairResult
{
    public bool Success { get; init; }

    public IReadOnlyList<string> RepairedFiles { get; init; } = [];

    public IReadOnlyList<string> FailedFiles { get; init; } = [];

    public int SlicesReconstructed { get; init; }

    public string? Message { get; init; }
}

/// <summary>
/// Rebuilds damaged and missing files of a verified PAR2 set.
/// </summary>
/// <remarks>
/// <para><b>Math.</b> Let M be the set of missing slices (|M| = L) and choose L recovery slices with exponents
/// e_1..e_L. For each chosen recovery slice</para>
/// <code>R_e = Σ_{i known} c(i,e)·D_i  +  Σ_{j ∈ M} c(j,e)·D_j        with c(i,e) = base_i^e</code>
/// <para>Moving the known part to the left (subtraction is XOR):</para>
/// <code>S_e := R_e + Σ_{i known} c(i,e)·D_i  =  Σ_{j ∈ M} c(j,e)·D_j      i.e.  S = A·D_M</code>
/// <para>with the L×L matrix <c>A[e][j] = c(j,e)</c>. So <c>D_M = A⁻¹·S</c>. This is done in two streaming passes
/// per slice range: pass 1 computes S (reading all known slices once), pass 2 multiplies by A⁻¹ in memory and
/// writes the rebuilt slices into the output files. If A happens to be singular (possible with PAR2's
/// non-systematic matrix), another choice of recovery slices is tried.</para>
/// </remarks>
public static class Par2Repairer
{
    public static Par2RepairResult Repair(Par2VerifyResult verify, Par2RepairOptions? options = null, OperationContext? context = null)
    {
        options ??= new Par2RepairOptions();
        context ??= OperationContext.Default;
        var observer = context.Observer;
        var set = verify.Set;
        var map = context.Blocks;

        if (verify.AllFilesComplete)
        {
            return new Par2RepairResult { Success = true, Message = "All files are already correct." };
        }

        if (!verify.RepairPossible)
        {
            return new Par2RepairResult
            {
                Success = false,
                Message = verify.CriticalDataComplete
                    ? $"Repair not possible: {verify.MissingSlices} slice(s) missing but only {verify.RecoverySlicesAvailable} recovery slice(s) available."
                    : "Repair not possible: critical packets (file descriptions or slice checksums) are missing.",
            };
        }

        // Files whose stored name was rejected as unsafe (empty target) are never written; see Par2Verifier.
        // Non-recovery files have no slices: an empty one is recreated, another one only from a complete copy.
        static bool Restorable(Par2FileVerification f) => f.File.InRecoverySet || f.File.Length == 0 || f.FoundAs != null;
        var broken = verify.Files.Where(f => f.Status != Par2FileStatus.Complete).ToList();
        var toRebuild = broken.Where(f => f.TargetPath.Length > 0 && Restorable(f)).ToList();
        var unsafeNames = broken.Where(f => f.TargetPath.Length == 0).Select(f => f.File.Name).ToList();
        var unprotected = broken.Where(f => f.TargetPath.Length > 0 && !Restorable(f)).Select(f => f.TargetPath).ToList();
        var missing = Enumerable.Range(0, set.TotalSlices).Where(i => verify.SliceSources[i] == null).ToList();
        long rebuildBytes = Math.Max(1, toRebuild.Sum(f => f.File.Length));
        double sliceBytes = set.SliceSize;
        var stages = new List<OperationStage>();
        if (missing.Count > 0)
        {
            // Inverting an L × L matrix takes about L³ multiplications; a few hundred per byte read is a fair guess.
            stages.Add(new(RepairStages.Choosing, Math.Pow(missing.Count, 3) / 200 + 1, RepairStages.ChoosingDescription));
        }

        stages.Add(new(RepairStages.Copying, rebuildBytes, RepairStages.CopyingDescription));
        if (missing.Count > 0)
        {
            stages.Add(new(RepairStages.Reconstructing, (set.TotalSlices + missing.Count) * sliceBytes, RepairStages.ReconstructingDescription(missing.Count)));
        }

        stages.Add(new(RepairStages.VerifyingRepaired, rebuildBytes, RepairStages.VerifyingRepairedDescription));
        observer.Plan(stages);

        // Choose the recovery slices first, so nothing is written when the matrix cannot be inverted.
        if (missing.Count > 0)
        {
            observer.Stage(RepairStages.Choosing);
        }

        var selection = missing.Count > 0 ? ChooseRecoverySlices(set, missing, context.CancellationToken, p => observer.Progress(p)) : null;
        if (missing.Count > 0 && selection == null)
        {
            return new Par2RepairResult
            {
                Success = false,
                Message = $"Repair not possible: no invertible selection of {missing.Count} out of {verify.RecoverySlicesAvailable} recovery slice(s) was found.",
            };
        }

        // Temporary output files, pre-filled with every slice that exists somewhere. When anything fails (I/O error,
        // cancellation), they are removed again and the original files stay as they were.
        var temp = new Dictionary<Par2FileVerification, FileStream>();
        using var handles = new FileHandleCache();
        bool rebuilt = false;
        try
        {
            observer.Stage(RepairStages.Copying);
            long copied = 0;
            foreach (var fv in toRebuild)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fv.TargetPath)!);
                var fs = new FileStream(TempPathOf(fv), FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess);
                temp[fv] = fs;
                fs.SetLength(fv.File.Length);
            }

            Parallel.ForEach(toRebuild, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Threads), CancellationToken = context.CancellationToken }, fv =>
            {
                if (!fv.File.InRecoverySet)
                {
                    if (fv.FoundAs != null)
                    {
                        using var copy = File.OpenRead(fv.FoundAs);
                        copy.CopyTo(temp[fv]);
                    }

                    observer.Progress((double)Interlocked.Add(ref copied, fv.File.Length) / rebuildBytes);
                    return;
                }

                var buf = new byte[set.SliceSize];
                for (int s = 0; s < fv.File.SliceCount; s++)
                {
                    int idx = fv.File.FirstSlice + s;
                    if (verify.SliceSources[idx] is { } src)
                    {
                        // A source can be shorter than the slice: e.g. a file truncated inside a slice that ends
                        // in zeros still matches (checksums are over the zero-padded slice). The missing part is
                        // zeros, not whatever the buffer held from the previous slice.
                        long offset = s * set.SliceSize;
                        int len = (int)Math.Min(set.SliceSize, fv.File.Length - offset);
                        int have = Math.Min(len, src.Length);
                        handles.ReadPadded(src.Path, src.Offset, buf.AsSpan(0, have));
                        buf.AsSpan(have, len - have).Clear();
                        RandomAccess.Write(temp[fv].SafeFileHandle, buf.AsSpan(0, len), offset);
                    }

                    observer.Progress((double)Interlocked.Add(ref copied, Math.Min(set.SliceSize, fv.File.Length - s * set.SliceSize)) / rebuildBytes);
                }
            });

            if (selection is { } sel)
            {
                observer.Stage(RepairStages.Reconstructing);
                foreach (var m in missing)
                {
                    map?.Set(m, BlockState.Repairing);
                }

                Reconstruct(verify, missing, sel.Exponents, sel.Inverse, temp, handles, context);
            }

            foreach (var fs in temp.Values)
            {
                fs.Flush(true);
            }

            rebuilt = true;
        }
        finally
        {
            foreach (var (fv, fs) in temp)
            {
                fs.Dispose();
                if (!rebuilt)
                {
                    File.Delete(TempPathOf(fv));
                }
            }
        }

        // Verify the rebuilt files and put them in place.
        observer.Stage(RepairStages.VerifyingRepaired);
        var repaired = new List<string>();
        var failed = new List<string>();
        long checkedBytes = 0;
        foreach (var fv in toRebuild)
        {
            var tmpPath = TempPathOf(fv);
            Md5Hash hash;
            using (var fs = File.OpenRead(tmpPath))
            {
                hash = Md5Hash.FromBytes(MD5.HashData(fs));
            }

            checkedBytes += fv.File.Length;
            observer.Progress((double)checkedBytes / rebuildBytes, Path.GetFileName(fv.TargetPath));

            if (hash != fv.File.FileHash)
            {
                failed.Add(fv.TargetPath);
                File.Delete(tmpPath);
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

            File.Move(tmpPath, fv.TargetPath);
            fv.Status = Par2FileStatus.Complete;
            repaired.Add(fv.TargetPath);
            for (int s = 0; s < fv.File.SliceCount; s++)
            {
                int idx = fv.File.FirstSlice + s;
                if (verify.SliceSources[idx] == null)
                {
                    map?.Set(idx, BlockState.Repaired);
                }
            }
        }

        var messages = new List<string>();
        if (failed.Count > 0)
        {
            messages.Add($"{failed.Count} file(s) failed verification after repair.");
        }

        if (unsafeNames.Count > 0)
        {
            messages.Add($"{unsafeNames.Count} file(s) with an unsafe name were not restored: {string.Join(", ", unsafeNames)}.");
        }

        if (unprotected.Count > 0)
        {
            messages.Add($"{unprotected.Count} file(s) are not protected by recovery data and were not restored: {string.Join(", ", unprotected)}.");
        }

        return new Par2RepairResult
        {
            Success = messages.Count == 0,
            RepairedFiles = repaired,
            FailedFiles = [.. failed, .. unsafeNames, .. unprotected],
            SlicesReconstructed = missing.Count,
            Message = messages.Count == 0 ? $"Repaired {repaired.Count} file(s)." : string.Join(" ", messages),
        };
    }

    /// <summary>The temporary file a target is rebuilt in, next to it: "name.deveparttools.tmp".</summary>
    private static string TempPathOf(Par2FileVerification fv) => fv.TargetPath + ".deveparttools.tmp";

    /// <summary>"name.1", "name.2", ... (first that does not exist), like par2cmdline.</summary>
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

    /// <summary>
    /// Chooses L = |missing| recovery slices whose matrix <c>A[r][c] = base_missing[c] ^ e_r</c> is invertible: the
    /// first L exponents, else the window slides along the sorted exponents. Returns null when no window works.
    /// </summary>
    /// <example>
    /// Missing slices 0 and 2 (bases 2^1 and 2^4) with exponents {0, 21845, 30000}: the window {0, 21845} is
    /// singular, because <c>det = 2^21845·(16^21845 − 2^21845)·…</c> vanishes when (16/2)^21845 = 8^21845 = 1, and
    /// 8 = 2^3 has order 65535/3 = 21845. The next window {21845, 30000} is invertible and is used.
    /// </example>
    internal static (List<uint> Exponents, GfMatrix<ushort> Inverse)? ChooseRecoverySlices(Par2RecoverySet set, List<int> missing, CancellationToken cancellationToken, Action<double>? progress = null)
    {
        var field = new Gf16Field(Par2Math.Field);
        int l = missing.Count;
        var exponents = set.RecoverySlices.Keys.ToList();
        for (int start = 0; start + l <= exponents.Count; start++)
        {
            var candidate = exponents.GetRange(start, l);
            var a = new GfMatrix<ushort>(l, l, field);
            for (int r = 0; r < l; r++)
            {
                for (int c = 0; c < l; c++)
                {
                    a[r, c] = Par2Math.Coefficient(missing[c], candidate[r]);
                }
            }

            if (a.TryInvert(cancellationToken, progress))
            {
                return (candidate, a);
            }
        }

        return null;
    }

    private static void Reconstruct(Par2VerifyResult verify, List<int> missing, List<uint> chosen, GfMatrix<ushort> inverse, Dictionary<Par2FileVerification, FileStream> temp, FileHandleCache handles, OperationContext context)
    {
        var set = verify.Set;
        var field = new Gf16Field(Par2Math.Field);
        int l = missing.Count;
        var missingSet = new HashSet<int>(missing);
        var known = Enumerable.Range(0, set.TotalSlices).Where(i => !missingSet.Contains(i)).ToList();
        int n = known.Count + l;

        // Pass 1 inputs: known data slices, then the chosen recovery slices. Output r: S_r.
        var source = new RepairSource(verify, known, chosen.Select(e => set.RecoverySlices[e]).ToList(), handles);
        var coefficients = new Pass1Coefficients(known, chosen);

        // Owners of the missing slices for pass 2 output. A slice of a file that is not rebuilt (unsafe name) still
        // has to be solved for, but gets length 0, so TargetSink drops it.
        var targets = new (FileStream Stream, long Offset, long Length)[l];
        for (int j = 0; j < l; j++)
        {
            var fv = verify.Files.First(f => missing[j] >= f.File.FirstSlice && missing[j] < f.File.FirstSlice + f.File.SliceCount);
            long off = (missing[j] - fv.File.FirstSlice) * set.SliceSize;
            targets[j] = temp.TryGetValue(fv, out var stream) ? (stream, off, Math.Min(set.SliceSize, fv.File.Length - off)) : (null!, 0, 0);
        }

        var options = context.Combine.Create(context.Observer.ToProgress(), context.MemoryBudget / 3);
        var sink = new Pass2Sink(field, inverse, targets, l, options);
        LinearCombiner.Combine(field, coefficients, n, l, source, sink, set.SliceSize, options);
    }

    private sealed class RepairSource(Par2VerifyResult verify, List<int> known, List<Par2RecoverySlicePacket> recovery, FileHandleCache handles) : IRegionSource
    {
        public void Read(int index, long offset, Span<byte> destination)
        {
            if (index < known.Count)
            {
                var src = verify.SliceSources[known[index]]!.Value;
                long available = Math.Clamp(src.Length - offset, 0, destination.Length);
                handles.ReadPadded(src.Path, src.Offset + offset, destination[..(int)available]);
                destination[(int)available..].Clear();
            }
            else
            {
                var r = recovery[index - known.Count];
                long available = Math.Clamp(r.DataLength - offset, 0, destination.Length);
                handles.ReadPadded(r.DataFile, r.DataOffset + offset, destination[..(int)available]);
                destination[(int)available..].Clear();
            }
        }
    }

    /// <summary>S_r = R_r + Σ_known c(i, e_r)·D_i: known input i gets c(i, e_r), recovery input r' gets [r' == r].</summary>
    private sealed class Pass1Coefficients(List<int> known, List<uint> exponents) : ICoefficientProvider<ushort>
    {
        public void Fill(int output, int firstInput, Span<ushort> destination)
        {
            uint e = exponents[output];
            for (int i = 0; i < destination.Length; i++)
            {
                int input = firstInput + i;
                destination[i] = input < known.Count
                    ? Par2Math.Coefficient(known[input], e)
                    : (ushort)(input - known.Count == output ? 1 : 0);
            }
        }
    }

    /// <summary>Collects S for one slice range, then computes D = A⁻¹·S and writes it into the output files.</summary>
    private sealed class Pass2Sink(Gf16Field field, GfMatrix<ushort> inverse, (FileStream Stream, long Offset, long Length)[] targets, int l, LinearCombineOptions options) : IRegionSink
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

            int len = source.Length;
            var outSink = new TargetSink(targets, offset);
            LinearCombiner.Combine(field, new MatrixCoefficients<ushort>(inverse), l, l, new MemoryRegions(_s), outSink, len,
                new LinearCombineOptions { Threads = options.Threads, CancellationToken = options.CancellationToken, MemoryBudget = options.MemoryBudget });
        }
    }

    /// <summary>
    /// Writes rebuilt slice data into the temporary files. Only the slice's real length is written: the zero padding
    /// of a last slice (and everything of a length-0 target) is dropped.
    /// </summary>
    private sealed class TargetSink((FileStream Stream, long Offset, long Length)[] targets, long rangeOffset) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source)
        {
            var (stream, sliceOffset, length) = targets[index];
            long pos = rangeOffset + offset;
            if (pos >= length)
            {
                return;
            }

            int n = (int)Math.Min(source.Length, length - pos);
            RandomAccess.Write(stream.SafeFileHandle, source[..n], sliceOffset + pos);
        }
    }
}

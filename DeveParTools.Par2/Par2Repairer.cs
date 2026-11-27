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

        var toRebuild = verify.Files.Where(f => f.Status != Par2FileStatus.Complete && f.TargetPath.Length > 0).ToList();
        var missing = Enumerable.Range(0, set.TotalSlices).Where(i => verify.SliceSources[i] == null).ToList();
        observer.Stage("Repairing");

        // Temporary output files, pre-filled with every slice that exists somewhere.
        var temp = new Dictionary<Par2FileVerification, FileStream>();
        using var handles = new FileHandleCache();
        try
        {
            foreach (var fv in toRebuild)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fv.TargetPath)!);
                var tmpPath = fv.TargetPath + ".deveparttools.tmp";
                var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess);
                fs.SetLength(fv.File.Length);
                temp[fv] = fs;
            }

            Parallel.ForEach(toRebuild, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Threads), CancellationToken = context.CancellationToken }, fv =>
            {
                var buf = new byte[set.SliceSize];
                for (int s = 0; s < fv.File.SliceCount; s++)
                {
                    int idx = fv.File.FirstSlice + s;
                    if (verify.SliceSources[idx] is { } src)
                    {
                        long offset = s * set.SliceSize;
                        int len = (int)Math.Min(set.SliceSize, fv.File.Length - offset);
                        handles.ReadPadded(src.Path, src.Offset, buf.AsSpan(0, Math.Min(len, src.Length)));
                        RandomAccess.Write(temp[fv].SafeFileHandle, buf.AsSpan(0, len), offset);
                    }
                }
            });

            if (missing.Count > 0)
            {
                foreach (var m in missing)
                {
                    map?.Set(m, BlockState.Repairing);
                }

                Reconstruct(verify, missing, temp, handles, context);
            }

            foreach (var fs in temp.Values)
            {
                fs.Flush(true);
            }
        }
        finally
        {
            foreach (var fs in temp.Values)
            {
                fs.Dispose();
            }
        }

        // Verify the rebuilt files and put them in place.
        observer.Stage("Verifying repaired files");
        var repaired = new List<string>();
        var failed = new List<string>();
        foreach (var fv in toRebuild)
        {
            var tmpPath = fv.TargetPath + ".deveparttools.tmp";
            Md5Hash hash;
            using (var fs = File.OpenRead(tmpPath))
            {
                hash = Md5Hash.FromBytes(MD5.HashData(fs));
            }

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

        return new Par2RepairResult
        {
            Success = failed.Count == 0,
            RepairedFiles = repaired,
            FailedFiles = failed,
            SlicesReconstructed = missing.Count,
            Message = failed.Count == 0 ? $"Repaired {repaired.Count} file(s)." : $"{failed.Count} file(s) failed verification after repair.",
        };
    }

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

    private static void Reconstruct(Par2VerifyResult verify, List<int> missing, Dictionary<Par2FileVerification, FileStream> temp, FileHandleCache handles, OperationContext context)
    {
        var set = verify.Set;
        var field = new Gf16Field(Par2Math.Field);
        int l = missing.Count;
        var exponents = set.RecoverySlices.Keys.ToList();

        // Choose L recovery slices whose matrix is invertible (first L; on failure slide the window).
        GfMatrix<ushort>? inverse = null;
        List<uint>? chosen = null;
        for (int start = 0; start + l <= exponents.Count && inverse == null; start++)
        {
            var candidate = exponents.Skip(start).Take(l).ToList();
            var a = new GfMatrix<ushort>(l, l, field);
            for (int r = 0; r < l; r++)
            {
                for (int c = 0; c < l; c++)
                {
                    a[r, c] = Par2Math.Coefficient(missing[c], candidate[r]);
                }
            }

            if (a.TryInvert(context.CancellationToken))
            {
                inverse = a;
                chosen = candidate;
            }
        }

        if (inverse == null || chosen == null)
        {
            throw new InvalidOperationException("No invertible selection of recovery slices was found.");
        }

        var missingSet = new HashSet<int>(missing);
        var known = Enumerable.Range(0, set.TotalSlices).Where(i => !missingSet.Contains(i)).ToList();
        int n = known.Count + l;

        // Pass 1 inputs: known data slices, then the chosen recovery slices. Output r: S_r.
        var source = new RepairSource(verify, known, chosen.Select(e => set.RecoverySlices[e]).ToList(), handles);
        var coefficients = new Pass1Coefficients(known, chosen);

        // Owners of the missing slices for pass 2 output.
        var targets = new (FileStream Stream, long Offset, long Length)[l];
        for (int j = 0; j < l; j++)
        {
            var fv = verify.Files.First(f => missing[j] >= f.File.FirstSlice && missing[j] < f.File.FirstSlice + f.File.SliceCount);
            long off = (missing[j] - fv.File.FirstSlice) * set.SliceSize;
            targets[j] = (temp[fv], off, Math.Min(set.SliceSize, fv.File.Length - off));
        }

        var options = context.Combine.Create(new Progress<double>(p => context.Observer.Progress(p, "Reconstructing")), context.MemoryBudget / 3);
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

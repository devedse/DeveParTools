using System.Numerics;
using System.Runtime.InteropServices;
using DeveParTools.Core.Codes;
using DeveParTools.Core.Codes.Fft;
using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

/// <summary>
/// FFT ("PAR FFT") recovery for PAR3, compatible with par3cmdline. Input blocks are split into
/// <c>cohorts = interleave + 1</c> independent Leopard-RS codes: input block i belongs to cohort i mod C (at local
/// position i / C) and recovery block r to cohort r mod C (local index r / C). Cohorts let a set exceed the
/// 65536-symbol limit of GF(2^16) at the cost of a weaker guarantee (each cohort can only repair its own blocks).
/// </summary>
/// <remarks>
/// <para>Example with 7 input blocks, 4 recovery blocks and 2 cohorts:</para>
/// <code>
/// cohort 0: inputs 0 2 4 6 (local 0..3), recovery 0 2 (local 0 1)
/// cohort 1: inputs 1 3 5 + one zero block (local 0..3), recovery 1 3 (local 0 1)
/// </code>
/// <para>Losing inputs 2 and 4 is repairable (cohort 0 has 2 recovery blocks); losing 1, 3 and 5 is not, although
/// 4 recovery blocks exist in total.</para>
/// <para><b>Codes with m &gt; k.</b> Leopard is documented for recovery count ≤ original count (and our port checks
/// it), but par3cmdline also writes FFT codes with more recovery than input blocks per cohort, e.g. 20 recovery
/// blocks for 10 input blocks (m = 32). Such a code is the same as one with zero originals appended: Leopard encodes
/// the originals in groups of m and a group of zeros adds nothing, and NextPow2(m + k) is 2m either way. So every
/// cohort is coded with <see cref="Layout.LeopardOriginals"/> = max(k, m) originals, the extra ones zero.</para>
/// </remarks>
public static unsafe class Par3Fft
{
    public sealed record Layout(int Cohorts, int OriginalsPerCohort, int RecoveryPerCohort, LeopardField? Field)
    {
        /// <summary>m: recovery blocks computed per cohort (a power of two).</summary>
        public int M => LeopardCodec.NextPow2(RecoveryPerCohort);

        /// <summary>Originals passed to Leopard per cohort: the cohort's input blocks, padded with zero blocks to at least m.</summary>
        public int LeopardOriginals => Math.Max(OriginalsPerCohort, M);
    }

    /// <summary>
    /// Field selection like par3cmdline: XOR when only one recovery block can ever exist, FF8 when the code word fits
    /// in 256 positions, FF16 otherwise.
    /// </summary>
    public static Layout Plan(ulong blockCount, int maxRecovery, int cohorts)
    {
        int k2 = (int)((blockCount + (ulong)cohorts - 1) / (ulong)cohorts);
        int r2 = Math.Max(1, (maxRecovery + cohorts - 1) / cohorts);
        if (maxRecovery <= 1 && cohorts == 1)
        {
            return new Layout(1, k2, 1, null);
        }

        int m = LeopardCodec.NextPow2(r2);
        int n = LeopardCodec.NextPow2(m + k2);
        var field = n <= 256 ? LeopardField.FF8 : LeopardField.FF16;
        return new Layout(cohorts, k2, r2, field);
    }

    /// <summary>
    /// The layout a "PAR FFT" matrix packet describes: XOR when the Start packet's field size is 0, otherwise
    /// interleave + 1 cohorts of Leopard codes over GF(2^8) (field size 1) or GF(2^16) (field size 2), each with
    /// m = 2^MaxRecoveryLog2 recovery blocks. Null when Leopard cannot handle the parameters (other field sizes,
    /// m &gt; 32768, absurd interleave values).
    /// </summary>
    public static Layout? FromPacket(Par3FftMatrixPacket packet, int gfSize, ulong blockCount)
    {
        if (gfSize == 0)
        {
            return new Layout(1, (int)blockCount, 1, null);
        }

        if (gfSize > 2 || packet.MaxRecoveryLog2 > 15 || packet.Interleave >= int.MaxValue)
        {
            return null;
        }

        int cohorts = (int)packet.Interleave + 1;
        return new Layout(cohorts, (int)((blockCount + (ulong)cohorts - 1) / (ulong)cohorts), 1 << packet.MaxRecoveryLog2, gfSize == 1 ? LeopardField.FF8 : LeopardField.FF16);
    }

    /// <summary>
    /// Whether <see cref="Decode"/> can rebuild every missing block: each cohort needs at least as many usable recovery
    /// blocks (local index below m) as it has missing input blocks. The XOR code repairs at most one block, with
    /// recovery block 0.
    /// </summary>
    /// <example>
    /// Two cohorts, input blocks 1 and 3 missing (both odd, so both in cohort 1), recovery blocks 0 and 2 available
    /// (both even, cohort 0): two recovery blocks for two missing blocks, but cohort 1 has none, so repair fails.
    /// </example>
    public static bool CanDecode(Layout layout, ulong blockCount, bool[] available, IEnumerable<ulong> recoveryIndices)
    {
        var indices = recoveryIndices.ToHashSet();
        if (layout.Field == null)
        {
            int missing = available.Count(a => !a);
            return missing == 0 || (missing == 1 && indices.Contains(0));
        }

        for (int cohort = 0; cohort < layout.Cohorts; cohort++)
        {
            if (MissingInCohort(layout, blockCount, available, cohort).Count > UsableInCohort(layout, indices, cohort).Count)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Local positions (input i is local i / C) of the missing input blocks of a cohort.</summary>
    private static List<int> MissingInCohort(Layout layout, ulong blockCount, bool[] available, int cohort)
    {
        var missing = new List<int>();
        for (int j = 0; j < layout.OriginalsPerCohort; j++)
        {
            ulong block = (ulong)j * (ulong)layout.Cohorts + (ulong)cohort;
            if (block < blockCount && !available[block])
            {
                missing.Add(j);
            }
        }

        return missing;
    }

    /// <summary>Global recovery indices of a cohort that its code can use (local index r / C below m).</summary>
    private static List<ulong> UsableInCohort(Layout layout, IEnumerable<ulong> recoveryIndices, int cohort)
        => recoveryIndices.Where(r => (int)(r % (ulong)layout.Cohorts) == cohort && r / (ulong)layout.Cohorts < (ulong)layout.M).ToList();

    /// <summary>Smallest number of cohorts for which every cohort fits in GF(2^16) and has m ≤ k.</summary>
    public static int ChooseCohorts(ulong blockCount, int recoveryCount)
    {
        for (int c = 1; c < 1 << 20; c++)
        {
            ulong k2 = (blockCount + (ulong)c - 1) / (ulong)c;
            int r2 = Math.Max(1, (recoveryCount + c - 1) / c);
            int m = LeopardCodec.NextPow2(r2);
            if ((ulong)r2 <= k2 && (ulong)LeopardCodec.NextPow2(m + (int)Math.Min(k2, int.MaxValue / 4)) <= 65536)
            {
                return c;
            }
        }

        throw new ArgumentException("Too many blocks for the FFT code.");
    }

    /// <summary>
    /// Computes the requested recovery blocks (global indices) and hands them to <paramref name="sink"/>, processing the
    /// block in slices (passes) so that the buffers of a cohort fit the memory budget (see <see cref="RunPasses"/>).
    /// </summary>
    public static void Encode(Layout layout, ulong blockCount, IReadOnlyList<ulong> recoveryIndices, IRegionSource blocks, IRegionSink sink, long blockSize, LinearCombineOptions options)
    {
        int c = layout.Cohorts;
        if (layout.Field == null)
        {
            // Plain XOR of all input blocks.
            var ones = new XorCoefficients();
            LinearCombiner.Combine(new Core.Galois.Gf8Field(Core.Galois.GaloisField8.Default), ones, (int)blockCount, 1, blocks, sink, blockSize, options);
            return;
        }

        var position = new Dictionary<ulong, int>();
        for (int i = 0; i < recoveryIndices.Count; i++)
        {
            position[recoveryIndices[i]] = i;
        }

        int k2 = layout.LeopardOriginals;
        int m = layout.M;
        int workCount = LeopardCodec.EncodeWorkCount(k2, m);
        long slice = SliceLength(options.MemoryBudget, Present(layout, blockCount, 0), workCount, blockSize);
        long slices = (blockSize + slice - 1) / slice;
        int cohortsWithWork = Enumerable.Range(0, c).Count(cohort => recoveryIndices.Any(r => (int)(r % (ulong)c) == cohort));
        var progress = new FftPassProgress(options.Progress, (int)(cohortsWithWork * slices), "computing the recovery data", "writing the recovery data");
        bool parallelWrites = sink is IConcurrentRegionSink;
        for (int cohort = 0; cohort < c; cohort++)
        {
            var wanted = recoveryIndices.Where(r => (int)(r % (ulong)c) == cohort).ToList();
            if (wanted.Count == 0)
            {
                continue;
            }

            // Input blocks of this cohort that exist; the rest of the k2 positions are zero padding.
            int present = Present(layout, blockCount, cohort);
            var work = Alloc(workCount, slice);
            try
            {
                RunPasses(present, slice, blockSize, progress, options.CancellationToken,
                    (i, offset, destination) => blocks.Read((int)((ulong)i * (ulong)c + (ulong)cohort), offset, destination),
                    (offset, len, lenAligned, inputs, zero) =>
                    {
                        var originals = new nint[k2];
                        for (int j = 0; j < k2; j++)
                        {
                            originals[j] = j < present ? inputs[j] : zero;
                        }

                        progress.Begin(FftPassProgress.Phase.Computing);
                        LeopardCodec.Encode(layout.Field, originals, m, work, lenAligned, options.Threads, progress.Computing);
                        progress.Begin(FftPassProgress.Phase.Writing);
                        int written = 0;
                        void Write(int i)
                        {
                            int local = (int)(wanted[i] / (ulong)c);
                            sink.Write(position[wanted[i]], offset, new ReadOnlySpan<byte>((byte*)work[local], (int)len));
                            int n = Interlocked.Increment(ref written);
                            if (n % 64 == 0 || n == wanted.Count)
                            {
                                progress.Writing(n, wanted.Count);
                            }
                        }

                        if (parallelWrites)
                        {
                            // The recovery files hash what they get: in parallel, that is no longer one core's work.
                            Parallel.For(0, wanted.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads), CancellationToken = options.CancellationToken }, Write);
                        }
                        else
                        {
                            for (int i = 0; i < wanted.Count; i++)
                            {
                                options.CancellationToken.ThrowIfCancellationRequested();
                                Write(i);
                            }
                        }
                    });
            }
            finally
            {
                Free(work);
            }
        }
    }

    /// <summary>
    /// Rebuilds missing input blocks. <paramref name="available"/>[i] says whether input block i can be read from
    /// <paramref name="blocks"/>; <paramref name="recovery"/> maps global recovery index → reader index in
    /// <paramref name="recoveryReader"/>. Rebuilt blocks go to <paramref name="sink"/> (index = block number).
    /// </summary>
    public static void Decode(Layout layout, ulong blockCount, bool[] available, IReadOnlyDictionary<ulong, int> recovery, IRegionSource blocks, IRegionSource recoveryReader, IRegionSink sink, long blockSize, LinearCombineOptions options)
    {
        int c = layout.Cohorts;
        if (layout.Field == null)
        {
            // XOR: the single missing block = recovery ^ all other blocks.
            int missing = Array.IndexOf(available, false);
            var inputs = new MixedSource(blocks, recoveryReader, available, recovery[0]);
            LinearCombiner.Combine(new Core.Galois.Gf8Field(Core.Galois.GaloisField8.Default), new XorCoefficients(), (int)blockCount + 1, 1, inputs, new FixedIndexSink(sink, missing), blockSize, options);
            return;
        }

        int k2 = layout.LeopardOriginals;
        int m = layout.M;
        int workCount = LeopardCodec.DecodeWorkCount(k2, m);

        // A cohort reads its available blocks and as many recovery blocks as it misses: never more than its blocks.
        long slice = SliceLength(options.MemoryBudget, Present(layout, blockCount, 0), workCount, blockSize);
        long slices = (blockSize + slice - 1) / slice;
        var withWork = Enumerable.Range(0, c).Where(cohort => MissingInCohort(layout, blockCount, available, cohort).Count > 0).ToList();
        for (int done = 0; done < withWork.Count; done++)
        {
            int cohort = withWork[done];
            var missingLocal = MissingInCohort(layout, blockCount, available, cohort);
            var share = new ShareProgress(options.Progress, (double)done / withWork.Count, 1.0 / withWork.Count);

            // Local recovery index → reader index.
            var recLocal = UsableInCohort(layout, recovery.Keys, cohort).ToDictionary(g => (int)(g / (ulong)c), g => recovery[g]);

            if (recLocal.Count < missingLocal.Count)
            {
                throw new InvalidOperationException($"Cohort {cohort}: {missingLocal.Count} blocks missing but only {recLocal.Count} recovery blocks.");
            }

            // Any recovery blocks will do, as many as there are missing blocks: reading more only costs time. (A file of
            // 201 blocks missing from a set with 5,316 recovery blocks: reading them all doubled the time of the repair.)
            var usedRecovery = recLocal.OrderBy(r => r.Key).Take(missingLocal.Count).ToList();

            // What is read: the cohort's available blocks (local positions), then the recovery blocks used.
            int present = Present(layout, blockCount, cohort);
            var knownLocal = Enumerable.Range(0, present).Where(j => available[(ulong)j * (ulong)c + (ulong)cohort]).ToList();
            int inputCount = knownLocal.Count + usedRecovery.Count;
            if (layout.Field.Bits == 16 && MatrixIsCheaper(missingLocal.Count, inputCount, workCount))
            {
                share.Detail = "computing the missing blocks (few are missing: directly, without the FFT)";
                DecodeByMatrix(layout, cohort, missingLocal, knownLocal, usedRecovery, blocks, recoveryReader, sink, blockSize, options, share);
                continue;
            }

            var progress = new FftPassProgress(share, (int)slices, "computing the missing blocks", "writing the rebuilt blocks");
            var work = Alloc(workCount, slice);
            try
            {
                RunPasses(inputCount, slice, blockSize, progress, options.CancellationToken,
                    (i, offset, destination) =>
                    {
                        if (i < knownLocal.Count)
                        {
                            blocks.Read((int)((ulong)knownLocal[i] * (ulong)c + (ulong)cohort), offset, destination);
                        }
                        else
                        {
                            recoveryReader.Read(usedRecovery[i - knownLocal.Count].Value, offset, destination);
                        }
                    },
                    (offset, len, lenAligned, inputs, zero) =>
                    {
                        // Missing originals and recovery blocks that are not used stay 0; positions past the cohort's
                        // blocks are known zero blocks.
                        var originals = new nint[k2];
                        for (int j = present; j < k2; j++)
                        {
                            originals[j] = zero;
                        }

                        for (int i = 0; i < knownLocal.Count; i++)
                        {
                            originals[knownLocal[i]] = inputs[i];
                        }

                        var rec = new nint[m];
                        for (int i = 0; i < usedRecovery.Count; i++)
                        {
                            rec[usedRecovery[i].Key] = inputs[knownLocal.Count + i];
                        }

                        progress.Begin(FftPassProgress.Phase.Computing);
                        LeopardCodec.Decode(layout.Field, originals, rec, work, lenAligned, options.Threads, progress.Computing);
                        progress.Begin(FftPassProgress.Phase.Writing);
                        for (int i = 0; i < missingLocal.Count; i++)
                        {
                            options.CancellationToken.ThrowIfCancellationRequested();
                            ulong block = (ulong)missingLocal[i] * (ulong)c + (ulong)cohort;
                            sink.Write((int)block, offset, new ReadOnlySpan<byte>((byte*)work[missingLocal[i]], (int)len));
                            if (i % 64 == 63 || i + 1 == missingLocal.Count)
                            {
                                progress.Writing(i + 1, missingLocal.Count);
                            }
                        }
                    });
            }
            finally
            {
                Free(work);
            }
        }
    }

    /// <summary>
    /// Whether rebuilding <paramref name="missing"/> blocks from <paramref name="inputs"/> blocks (the known ones and
    /// the recovery blocks used) is cheaper the way PAR2 does it (<see cref="DecodeByMatrix"/>) than with the FFT
    /// decoder of size <paramref name="n"/>.
    /// </summary>
    /// <remarks>
    /// <para>The matrix way costs missing × inputs multiply-adds per byte, so it grows with every missing block. The FFT
    /// costs about n·log2 n butterflies per byte however many are missing, and a butterfly costs about 8 times a
    /// multiply-add of the combiner (it is limited by memory, the combiner is not).</para>
    /// <para>Measured with 16 cores and the AVX2 kernel, 5,316 blocks of 510 KB with 5,316 FFT recovery blocks
    /// (n = 16,384): rebuilding 200 blocks took 3.1 s the matrix way and 4.8 s with the FFT, 400 blocks 5.4 s and
    /// 5.0 s, 800 blocks 12.1 s and 6.3 s. The rule puts the switch at 8 · 16,384 · 14 ÷ 5,316 ≈ 345 blocks.</para>
    /// </remarks>
    internal static bool MatrixIsCheaper(int missing, int inputs, int n)
        => (long)missing * inputs < 8L * n * BitOperations.Log2((uint)n);

    /// <summary>
    /// Rebuilds a cohort's missing blocks directly, as PAR2 does. The FFT code is linear: recovery block r is
    /// Σ G[r][i] · block i (<see cref="LeopardCodec.GeneratorRows"/>). With A the used recovery blocks' coefficients
    /// for the missing blocks and B those for the known blocks, A · missing = recovery + B · known, so
    /// missing = A⁻¹ · recovery + A⁻¹B · known: one linear combination of the blocks read, for the combiner.
    /// </summary>
    private static void DecodeByMatrix(Layout layout, int cohort, List<int> missingLocal, List<int> knownLocal, List<KeyValuePair<int, int>> usedRecovery,
        IRegionSource blocks, IRegionSource recoveryReader, IRegionSink sink, long blockSize, LinearCombineOptions options, IProgress<double> progress)
    {
        var leopard = layout.Field!;
        var field = leopard.Gf16;
        int c = layout.Cohorts, missing = missingLocal.Count, known = knownLocal.Count;
        var g = LeopardCodec.GeneratorRows(leopard, layout.LeopardOriginals, layout.M, usedRecovery.Select(r => r.Key).ToList(), options.Threads);
        var a = new GfMatrix<ushort>(missing, missing, field);
        var b = new GfMatrix<ushort>(missing, known, field);
        for (int t = 0; t < missing; t++)
        {
            for (int j = 0; j < missing; j++)
            {
                a[t, j] = (ushort)g[t][missingLocal[j]];
            }

            for (int i = 0; i < known; i++)
            {
                b[t, i] = (ushort)g[t][knownLocal[i]];
            }
        }

        // The FFT code is MDS: any recovery blocks of the cohort determine as many missing blocks, so A is invertible.
        _ = a.TryInvert(options.CancellationToken) ? 0 : throw new InvalidOperationException($"Cohort {cohort}: the recovery blocks do not determine the missing blocks.");

        var ab = a.Multiply(b);
        var coefficients = new GfMatrix<ushort>(missing, known + missing, field);
        for (int j = 0; j < missing; j++)
        {
            ab.Row(j).CopyTo(coefficients.Row(j));
            a.Row(j).CopyTo(coefficients.Row(j)[known..]);
        }

        var source = new CohortSource(blocks, recoveryReader, knownLocal.Select(j => (int)((ulong)j * (ulong)c + (ulong)cohort)).ToList(), usedRecovery.Select(r => r.Value).ToList());
        var target = new CohortSink(sink, missingLocal.Select(j => (int)((ulong)j * (ulong)c + (ulong)cohort)).ToList());
        LinearCombiner.Combine(field, new MatrixCoefficients<ushort>(coefficients), known + missing, missing, source, target, blockSize,
            new LinearCombineOptions { MemoryBudget = options.MemoryBudget, Threads = options.Threads, CancellationToken = options.CancellationToken, Progress = progress });
    }

    /// <summary>A cohort's known blocks (global indices), then its recovery blocks used (reader indices).</summary>
    private sealed class CohortSource(IRegionSource blocks, IRegionSource recovery, List<int> known, List<int> recoveryReaders) : IRegionSource
    {
        public void Read(int index, long offset, Span<byte> destination)
        {
            if (index < known.Count)
            {
                blocks.Read(known[index], offset, destination);
            }
            else
            {
                recovery.Read(recoveryReaders[index - known.Count], offset, destination);
            }
        }
    }

    /// <summary>Output j of the combination is missing block <c>missing[j]</c> (global index).</summary>
    private sealed class CohortSink(IRegionSink sink, List<int> missing) : IRegionSink
    {
        public void Write(int index, long offset, ReadOnlySpan<byte> source) => sink.Write(missing[index], offset, source);
    }

    /// <summary>One cohort's share [start, start + share) of the progress; <see cref="Detail"/> for reports without one.</summary>
    private sealed class ShareProgress(IProgress<double>? inner, double start, double share) : IDetailedProgress
    {
        public string? Detail { get; set; }

        public void Report(double value) => Report(value, Detail);

        public void Report(double value, string? detail) => inner.ReportDetail(start + share * Math.Clamp(value, 0, 1), detail);
    }

    /// <summary>The input blocks cohort <paramref name="cohort"/> really has (at most k2; the rest are zero padding).</summary>
    private static int Present(Layout layout, ulong blockCount, int cohort)
    {
        ulong c = (ulong)layout.Cohorts;
        return (int)Math.Min((ulong)layout.LeopardOriginals, (blockCount - (ulong)cohort + c - 1) / c);
    }

    /// <summary>
    /// The slice of every block a pass works on: the work buffers, two sets of input buffers (one being computed, one
    /// being read) and one zero buffer must fit the memory budget. A multiple of 64 bytes, at most the block.
    /// </summary>
    internal static long SliceLength(long memoryBudget, int inputs, int workCount, long blockSize)
    {
        long slice = Math.Max(64, memoryBudget / Math.Max(1, 2L * inputs + workCount + 1)) / 64 * 64;
        return Math.Min(slice, (blockSize + 63) / 64 * 64);
    }

    /// <summary>
    /// Runs the passes of an FFT encode or repair: pass p reads <paramref name="inputCount"/> input slices (bytes
    /// [offset, offset + slice) of every input block), then <paramref name="compute"/> computes and writes the pass.
    /// </summary>
    /// <remarks>
    /// The next pass's slices are read in the background, into a second set of buffers, while the current pass is
    /// computed and written: the disk and the processor work at the same time instead of taking turns. (Repairing
    /// 201 blocks from a set of 5,316 blocks of 2.69 MiB took 33 passes, each reading a 96 KB slice of every block
    /// and then computing; the processor was idle while each pass read.) The progress of the reading phase is what is
    /// left of the background read once the pass before is done, often nothing.
    /// </remarks>
    /// <param name="read">Reads input i's bytes [offset, offset + destination.Length); past its end, zeros.</param>
    /// <param name="compute">Computes and writes one pass: (offset, length, length rounded up to 64 bytes, the input
    /// slices, a zero slice for the padding positions).</param>
    private static void RunPasses(int inputCount, long slice, long blockSize, FftPassProgress progress, CancellationToken token,
        Action<int, long, Span<byte>> read, Action<long, long, long, nint[], nint> compute)
    {
        nint[][] sets = [Alloc(inputCount, slice), Alloc(inputCount, slice)];
        nint zero = (nint)NativeMemory.AlignedAlloc((nuint)slice, 64);
        new Span<byte>((void*)zero, (int)slice).Clear();
        int readCount = 0;
        Task? reader = null;
        try
        {
            void ReadPass(long offset, nint[] set)
            {
                long len = Math.Min(slice, blockSize - offset);
                long lenAligned = (len + 63) / 64 * 64;
                for (int i = 0; i < inputCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var span = new Span<byte>((byte*)set[i], (int)lenAligned);
                    read(i, offset, span[..(int)len]);
                    span[(int)len..].Clear();
                    Volatile.Write(ref readCount, i + 1);
                }
            }

            void StartRead(long offset, nint[] set)
            {
                Volatile.Write(ref readCount, 0);
                reader = Task.Run(() => ReadPass(offset, set), CancellationToken.None);
            }

            // Waits for the read of the pass that starts now, showing how far it got.
            void FinishRead()
            {
                while (Task.WaitAny([reader!], 50, token) < 0)
                {
                    progress.Reading(Volatile.Read(ref readCount), inputCount);
                }

                var done = reader!;
                reader = null;
                done.GetAwaiter().GetResult();
                progress.Reading(inputCount, inputCount);
            }

            StartRead(0, sets[0]);
            int pass = 0;
            for (long offset = 0; offset < blockSize; offset += slice, pass++)
            {
                progress.StartPass();
                FinishRead();
                if (offset + slice < blockSize)
                {
                    StartRead(offset + slice, sets[(pass + 1) & 1]);
                }

                long len = Math.Min(slice, blockSize - offset);
                compute(offset, len, (len + 63) / 64 * 64, sets[pass & 1], zero);
                progress.EndPass();
            }
        }
        finally
        {
            if (reader != null)
            {
                // The read writes into the buffers: it must be done before they are freed. Its own exception (if any)
                // is superseded by the one propagating now.
                Task.WaitAny(reader);
            }

            Free(sets[0]);
            Free(sets[1]);
            NativeMemory.AlignedFree((void*)zero);
        }
    }

    private static nint[] Alloc(int count, long size)
    {
        var a = new nint[count];
        for (int i = 0; i < count; i++)
        {
            a[i] = (nint)NativeMemory.AlignedAlloc((nuint)size, 64);
        }

        return a;
    }

    private static void Free(nint[] buffers)
    {
        foreach (var b in buffers)
        {
            NativeMemory.AlignedFree((void*)b);
        }
    }

    private sealed class XorCoefficients : ICoefficientProvider<byte>
    {
        public void Fill(int output, int firstInput, Span<byte> destination) => destination.Fill(1);
    }

    private sealed class FixedIndexSink(IRegionSink inner, int index) : IRegionSink
    {
        public void Write(int i, long offset, ReadOnlySpan<byte> source) => inner.Write(index, offset, source);
    }

    /// <summary>Inputs 0..k-1 are blocks (missing ones read as zero), input k is the recovery block.</summary>
    private sealed class MixedSource(IRegionSource blocks, IRegionSource recovery, bool[] available, int recoveryReader) : IRegionSource
    {
        public void Read(int index, long offset, Span<byte> destination)
        {
            if (index == available.Length)
            {
                recovery.Read(recoveryReader, offset, destination);
            }
            else if (available[index])
            {
                blocks.Read(index, offset, destination);
            }
            else
            {
                destination.Clear();
            }
        }
    }
}

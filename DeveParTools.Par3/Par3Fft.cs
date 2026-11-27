using System.Runtime.InteropServices;
using DeveParTools.Core.Codes;
using DeveParTools.Core.Codes.Fft;
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
    /// block in slices so that (k + 2m) slices of every cohort fit the memory budget.
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
        long slice = Math.Max(64, options.MemoryBudget / Math.Max(1, k2 + workCount)) / 64 * 64;
        slice = Math.Min(slice, (blockSize + 63) / 64 * 64);
        for (int cohort = 0; cohort < c; cohort++)
        {
            var wanted = recoveryIndices.Where(r => (int)(r % (ulong)c) == cohort).ToList();
            if (wanted.Count == 0)
            {
                continue;
            }

            var buffers = Alloc(k2 + workCount, slice);
            try
            {
                for (long offset = 0; offset < blockSize; offset += slice)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    long len = Math.Min(slice, blockSize - offset);
                    long lenAligned = (len + 63) / 64 * 64;
                    var originals = new nint[k2];
                    for (int j = 0; j < k2; j++)
                    {
                        ulong block = (ulong)j * (ulong)c + (ulong)cohort;
                        originals[j] = buffers[j];
                        var span = new Span<byte>((byte*)buffers[j], (int)lenAligned);
                        span.Clear();
                        if (block < blockCount)
                        {
                            blocks.Read((int)block, offset, span[..(int)len]);
                        }

                        // Reading is most of the time (the blocks of a cohort are spread over all files), so report it
                        // as it goes: 90% of this slice's share for reading, the rest when the slice is encoded.
                        if (j % 512 == 511)
                        {
                            options.Progress?.Report((cohort + (offset + 0.9 * len * (j + 1.0) / k2) / blockSize) / c);
                        }
                    }

                    var work = buffers.AsSpan(k2, workCount).ToArray();
                    LeopardCodec.Encode(layout.Field, originals, m, work, lenAligned, options.Threads);
                    foreach (var r in wanted)
                    {
                        int local = (int)(r / (ulong)c);
                        sink.Write(position[r], offset, new ReadOnlySpan<byte>((byte*)work[local], (int)len));
                    }

                    options.Progress?.Report((cohort + (double)(offset + len) / blockSize) / c);
                }
            }
            finally
            {
                Free(buffers);
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
        long slice = Math.Max(64, options.MemoryBudget / Math.Max(1, k2 + m + workCount)) / 64 * 64;
        slice = Math.Min(slice, (blockSize + 63) / 64 * 64);
        for (int cohort = 0; cohort < c; cohort++)
        {
            var missingLocal = MissingInCohort(layout, blockCount, available, cohort);
            if (missingLocal.Count == 0)
            {
                continue;
            }

            // Local recovery index → reader index.
            var recLocal = UsableInCohort(layout, recovery.Keys, cohort).ToDictionary(g => (int)(g / (ulong)c), g => recovery[g]);

            if (recLocal.Count < missingLocal.Count)
            {
                throw new InvalidOperationException($"Cohort {cohort}: {missingLocal.Count} blocks missing but only {recLocal.Count} recovery blocks.");
            }

            var buffers = Alloc(k2 + m + workCount, slice);
            try
            {
                for (long offset = 0; offset < blockSize; offset += slice)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    long len = Math.Min(slice, blockSize - offset);
                    long lenAligned = (len + 63) / 64 * 64;
                    var originals = new nint[k2];
                    for (int j = 0; j < k2; j++)
                    {
                        ulong block = (ulong)j * (ulong)c + (ulong)cohort;
                        var span = new Span<byte>((byte*)buffers[j], (int)lenAligned);
                        span.Clear();
                        if (block >= blockCount)
                        {
                            originals[j] = buffers[j]; // dummy zero block (known)
                        }
                        else if (available[block])
                        {
                            blocks.Read((int)block, offset, span[..(int)len]);
                            originals[j] = buffers[j];
                        }

                        // As in Encode: report the reading, which is most of the time.
                        if (j % 512 == 511)
                        {
                            options.Progress?.Report((cohort + (offset + 0.9 * len * (j + 1.0) / k2) / blockSize) / c);
                        }
                    }

                    var rec = new nint[m];
                    foreach (var (local, reader) in recLocal)
                    {
                        var span = new Span<byte>((byte*)buffers[k2 + local], (int)lenAligned);
                        span.Clear();
                        recoveryReader.Read(reader, offset, span[..(int)len]);
                        rec[local] = buffers[k2 + local];
                    }

                    var work = buffers.AsSpan(k2 + m, workCount).ToArray();
                    LeopardCodec.Decode(layout.Field, originals, rec, work, lenAligned, options.Threads);
                    foreach (int j in missingLocal)
                    {
                        ulong block = (ulong)j * (ulong)c + (ulong)cohort;
                        sink.Write((int)block, offset, new ReadOnlySpan<byte>((byte*)work[j], (int)len));
                    }

                    options.Progress?.Report((cohort + (double)(offset + len) / blockSize) / c);
                }
            }
            finally
            {
                Free(buffers);
            }
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

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Codes.Fft;

/// <summary>
/// FFT-based systematic Reed-Solomon erasure code in O(n log n), a port of Leopard-RS
/// (Lin, Chung &amp; Han, "Novel Polynomial Basis and Its Application to Reed-Solomon Erasure Codes").
/// Bit-compatible with the Leopard library used by par3cmdline's "PAR FFT" packet.
/// </summary>
/// <remarks>
/// <para><b>Idea.</b> The k original blocks and the recovery blocks are all values of one polynomial of degree
/// &lt; k, evaluated at different points of the field. Encoding interpolates the polynomial from the originals
/// (inverse FFT) and evaluates it at the recovery points (FFT). Matrix-based RS needs k·m multiplications per symbol;
/// here it is O((k + m) log m).</para>
/// <para><b>Layout of the code word</b> (positions are field elements in the Cantor basis):</para>
/// <code>
/// position: [0 .. m)                  [m .. m + k)          [m + k .. n)
///           recovery blocks (m = NextPow2(recovery count))   originals   zero padding
/// </code>
/// <para>Encoding processes the originals in groups of m: each group is IFFT'd with the matching "skew" offset
/// and XOR-accumulated, then one FFT of size m produces the recovery blocks. A recovery block's value only depends
/// on m and the data, so asking for fewer recovery blocks (but the same m) gives the same first blocks.</para>
/// <para><b>Decoding</b> without matrix inversion:</para>
/// <list type="number">
/// <item>mark erased positions (missing originals, missing recovery blocks, and the unused [count, m) slots);</item>
/// <item>evaluate the error-locator polynomial at every position with two Walsh-Hadamard transforms of the
/// logarithm table (this is where the Cantor basis pays off);</item>
/// <item>multiply every received block by its locator value, IFFT, take the formal derivative (a cheap XOR
/// pattern), FFT;</item>
/// <item>divide the result at each erased original position by its locator value → the original.</item>
/// </list>
/// <para>Every step is either a 2-element "butterfly" on whole blocks (<c>x ^= y·c; y ^= x</c>) or an XOR, so the
/// SIMD region kernels do all the heavy lifting. The region is split into independent byte ranges that are
/// processed in parallel (every symbol position is an independent codeword).</para>
/// </remarks>
public static unsafe class LeopardCodec
{
    public static int NextPow2(int v)
    {
        int r = 1;
        while (r < v)
        {
            r <<= 1;
        }

        return r;
    }

    /// <summary>Number of work buffers <see cref="Encode"/> needs.</summary>
    public static int EncodeWorkCount(int originalCount, int recoveryCount)
    {
        if (originalCount == 1)
        {
            return recoveryCount;
        }

        if (recoveryCount == 1)
        {
            return 1;
        }

        return NextPow2(recoveryCount) * 2;
    }

    /// <summary>Number of work buffers <see cref="Decode"/> needs.</summary>
    public static int DecodeWorkCount(int originalCount, int recoveryCount)
    {
        if (originalCount == 1 || recoveryCount == 1)
        {
            return originalCount;
        }

        int m = NextPow2(recoveryCount);
        return NextPow2(m + originalCount);
    }

    /// <summary>The field Leopard selects for a code of this size (FF8 when the code word fits in 256).</summary>
    public static LeopardField SelectField(int originalCount, int recoveryCount)
    {
        int m = NextPow2(recoveryCount);
        int n = NextPow2(m + originalCount);
        return n <= 256 ? LeopardField.FF8 : LeopardField.FF16;
    }

    public static void Validate(LeopardField field, int originalCount, int recoveryCount)
    {
        if (originalCount < 1 || recoveryCount < 1 || recoveryCount > originalCount)
        {
            throw new ArgumentException("Leopard requires 1 ≤ recovery count ≤ original count.");
        }

        int m = NextPow2(recoveryCount);
        if (originalCount > 1 && recoveryCount > 1 && NextPow2(m + originalCount) > field.Order)
        {
            throw new ArgumentException($"Too many blocks for GF(2^{field.Bits}): NextPow2(m + k) must be ≤ {field.Order}.");
        }
    }

    /// <summary>
    /// Computes <paramref name="recoveryCount"/> recovery blocks of <paramref name="bytes"/> bytes each.
    /// On return, recovery block i is in <c>work[i]</c>. <paramref name="work"/> needs
    /// <see cref="EncodeWorkCount"/> buffers.
    /// </summary>
    /// <param name="progress">Called with the fraction done (0..1) after each transform of each thread's part, from those threads.</param>
    public static void Encode(LeopardField field, ReadOnlySpan<nint> originals, int recoveryCount, ReadOnlySpan<nint> work, long bytes, int threads = 1, Action<double>? progress = null)
    {
        int k = originals.Length;
        Validate(field, k, recoveryCount);
        var orig = originals.ToArray();
        var w = work.ToArray();

        // Steps per part: one transform per group of m originals, then the final FFT (or one copy/XOR step).
        int steps = k == 1 || recoveryCount == 1 ? 1 : (k + NextPow2(recoveryCount) - 1) / NextPow2(recoveryCount) + 1;
        var counter = StepCounter.For(progress, RangeCount(bytes, threads) * steps);
        RunParallel(bytes, threads, (ofs, len) => EncodeRange(field, orig, recoveryCount, w, ofs, len, counter));
    }

    /// <summary>
    /// Recovers missing originals (entries equal to 0 in <paramref name="originals"/>) from the available recovery
    /// blocks (0 = missing). Recovered original i is written to <c>work[i]</c>. <paramref name="work"/> needs
    /// <see cref="DecodeWorkCount"/> buffers. Requires missing originals ≤ available recovery blocks.
    /// </summary>
    /// <param name="progress">Called with the fraction done (0..1) as each thread's part gets through the transforms, from those threads.</param>
    public static void Decode(LeopardField field, ReadOnlySpan<nint> originals, ReadOnlySpan<nint> recovery, ReadOnlySpan<nint> work, long bytes, int threads = 1, Action<double>? progress = null)
    {
        int k = originals.Length;
        int rc = recovery.Length;
        Validate(field, k, rc);
        int missing = 0, available = 0;
        foreach (var o in originals)
        {
            missing += o == 0 ? 1 : 0;
        }

        foreach (var r in recovery)
        {
            available += r == 0 ? 0 : 1;
        }

        if (missing > available)
        {
            throw new InvalidOperationException($"Cannot recover {missing} blocks with {available} recovery blocks.");
        }

        var orig = originals.ToArray();
        var rec = recovery.ToArray();
        var w = work.ToArray();
        if (missing == 0)
        {
            return;
        }

        if (k == 1)
        {
            // Every recovery block is a copy of the single original.
            nint src = rec.First(r => r != 0);
            var copied = StepCounter.For(progress, RangeCount(bytes, threads));
            RunParallel(bytes, threads, (ofs, len) =>
            {
                Copy((byte*)w[0] + ofs, (byte*)src + ofs, len);
                copied?.Step();
            });
            return;
        }

        if (rc == 1)
        {
            // The single recovery block is the XOR of all originals.
            int lost = Array.IndexOf(orig, (nint)0);
            var xored = StepCounter.For(progress, RangeCount(bytes, threads));
            RunParallel(bytes, threads, (ofs, len) =>
            {
                Copy((byte*)w[lost] + ofs, (byte*)rec[0] + ofs, len);
                for (int i = 0; i < k; i++)
                {
                    if (i != lost)
                    {
                        Xor((byte*)w[lost] + ofs, (byte*)orig[i] + ofs, len);
                    }
                }

                xored?.Step();
            });
            return;
        }

        int m = NextPow2(rc);
        int n = NextPow2(m + k);
        var errorLocations = ComputeErrorLocations(field, orig, rec, m);

        // Per part: the IFFT and the FFT are nearly all the work; the derivative and the last multiply are quick.
        var counter = StepCounter.For(progress, RangeCount(bytes, threads) * DecodeStepWeight);
        RunParallel(bytes, threads, (ofs, len) => DecodeRange(field, orig, rec, w, m, n, errorLocations, ofs, len, counter));
    }

    /// <summary>Weight of one part's decode: 9 (prepare + IFFT) + 1 (derivative) + 9 (FFT) + 1 (reveal).</summary>
    private const int DecodeStepWeight = 20;

    /// <summary>
    /// Counts finished steps of all threads' parts and reports them as a fraction (thread-safe). Null without a
    /// progress callback, so the codec pays nothing then.
    /// </summary>
    private sealed class StepCounter(Action<double> progress, long total)
    {
        private long _done;

        public static StepCounter? For(Action<double>? progress, long total) => progress == null ? null : new StepCounter(progress, Math.Max(1, total));

        public void Step(int weight = 1) => progress(Math.Min(1, (double)Interlocked.Add(ref _done, weight) / total));
    }

    /// <summary>
    /// Values of the error-locator polynomial (as logarithms) at every position, computed with two Walsh-Hadamard
    /// transforms: positions flagged 1 are erasures.
    /// </summary>
    private static int[] ComputeErrorLocations(LeopardField field, nint[] originals, nint[] recovery, int m)
    {
        var err = new int[field.Order];
        for (int i = 0; i < recovery.Length; i++)
        {
            if (recovery[i] == 0)
            {
                err[i] = 1;
            }
        }

        for (int i = recovery.Length; i < m; i++)
        {
            err[i] = 1;
        }

        for (int i = 0; i < originals.Length; i++)
        {
            if (originals[i] == 0)
            {
                err[i + m] = 1;
            }
        }

        field.Fwht(err, field.Order, m + originals.Length);
        for (int i = 0; i < field.Order; i++)
        {
            err[i] = (int)((long)err[i] * field.LogWalsh[i] % field.Modulus);
        }

        field.Fwht(err, field.Order, field.Order);
        return err;
    }

    /// <summary>Bytes of every buffer one part works on: a column of the transform (256 buffers) fits a core's L2 cache.</summary>
    private const long PartBytes = 1024;

    /// <summary>
    /// How many parts <see cref="RunParallel"/> splits <paramref name="bytes"/> into, and how long each is: parts of
    /// a multiple of <see cref="PartBytes"/> (smaller ones, a multiple of 64 bytes, only to give every thread one).
    /// A slice of 317,888 bytes cut into parts of 1,088 bytes made a decode pass twice as slow as parts of 1,024: the
    /// parts did not line up with the 4 KiB pages and cache sets their buffers sit in.
    /// </summary>
    private static (int Count, long Per) Ranges(long bytes, int threads)
    {
        long chunks = Math.Max(1, Math.Max(bytes / PartBytes, Math.Min(Math.Max(1, threads), bytes / 256)));
        long per = (bytes + chunks - 1) / chunks;
        long align = per >= PartBytes ? PartBytes : 64;
        per = (per + align - 1) / align * align;
        return ((int)((bytes + per - 1) / per), per);
    }

    private static int RangeCount(long bytes, int threads) => Math.Max(1, Ranges(bytes, threads).Count);

    private static void RunParallel(long bytes, int threads, Action<long, long> body)
    {
        var (count, per) = Ranges(bytes, threads);
        if (count <= 1)
        {
            body(0, bytes);
            return;
        }

        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, c =>
        {
            long ofs = c * per;
            body(ofs, Math.Min(per, bytes - ofs));
        });
    }

    /// <summary>Symbol positions <see cref="GeneratorRows"/> encodes at once.</summary>
    private const int GeneratorChunk = 1024;

    /// <summary>
    /// Rows of the code's generator matrix: <c>result[t][i]</c> is the coefficient of original i in recovery block
    /// <paramref name="recoveryRows"/>[t]. The code is linear, so recovery r = Σ_i G[r][i] · original i, which lets a
    /// repair with few missing blocks solve for them directly (as PAR2 does) instead of running the FFT decoder.
    /// </summary>
    /// <remarks>
    /// Found by encoding "unit" originals: in regions of P symbols, original i holds the symbol 1 at position i − p0 and
    /// zeros elsewhere, so recovery r holds G[r][i] at that position. P = 1,024 positions per encode (the other
    /// originals are one shared zero region), so for 8,192 originals and 8,192 recovery blocks that is 8 encodes of
    /// 2 KiB regions, a fraction of a second.
    /// </remarks>
    public static int[][] GeneratorRows(LeopardField field, int originalCount, int recoveryCount, IReadOnlyList<int> recoveryRows, int threads = 1)
    {
        Validate(field, originalCount, recoveryCount);
        int sym = field.SymbolSize;
        int per = Math.Min(GeneratorChunk, originalCount);
        long bytes = ((long)per * sym + 63) / 64 * 64;
        var rows = recoveryRows.Select(_ => new int[originalCount]).ToArray();
        var units = new nint[per];
        var work = new nint[EncodeWorkCount(originalCount, recoveryCount)];
        nint zero = (nint)NativeMemory.AlignedAlloc((nuint)bytes, 64);
        try
        {
            new Span<byte>((void*)zero, (int)bytes).Clear();
            for (int i = 0; i < units.Length; i++)
            {
                units[i] = (nint)NativeMemory.AlignedAlloc((nuint)bytes, 64);
            }

            for (int i = 0; i < work.Length; i++)
            {
                work[i] = (nint)NativeMemory.AlignedAlloc((nuint)bytes, 64);
            }

            var originals = new nint[originalCount];
            for (int first = 0; first < originalCount; first += per)
            {
                int count = Math.Min(per, originalCount - first);
                Array.Fill(originals, zero);
                for (int j = 0; j < count; j++)
                {
                    var unit = new Span<byte>((void*)units[j], (int)bytes);
                    unit.Clear();
                    unit[j * sym] = 1; // the symbol 1 (little-endian), the field's identity
                    originals[first + j] = units[j];
                }

                Encode(field, originals, recoveryCount, work, bytes, threads);
                for (int t = 0; t < recoveryRows.Count; t++)
                {
                    byte* r = (byte*)work[recoveryRows[t]];
                    for (int j = 0; j < count; j++)
                    {
                        rows[t][first + j] = sym == 2 ? r[2 * j] | (r[2 * j + 1] << 8) : r[j];
                    }
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree((void*)zero);
            foreach (var b in units.Concat(work))
            {
                NativeMemory.AlignedFree((void*)b);
            }
        }

        return rows;
    }

    // ------------------------------------------------------------------ encode

    private static void EncodeRange(LeopardField field, nint[] originals, int recoveryCount, nint[] work, long ofs, long len, StepCounter? counter)
    {
        int k = originals.Length;
        if (k == 1)
        {
            for (int i = 0; i < recoveryCount; i++)
            {
                Copy((byte*)work[i] + ofs, (byte*)originals[0] + ofs, len);
            }

            counter?.Step();
            return;
        }

        if (recoveryCount == 1)
        {
            Copy((byte*)work[0] + ofs, (byte*)originals[0] + ofs, len);
            for (int i = 1; i < k; i++)
            {
                Xor((byte*)work[0] + ofs, (byte*)originals[i] + ofs, len);
            }

            counter?.Step();
            return;
        }

        var ops = RegionOps.For(field, ofs, len);
        int m = NextPow2(recoveryCount);
        int skew = m - 1;

        // work[0..m) = IFFT(first group of originals)
        IfftDitEncoder(ops, originals, 0, Math.Min(k, m), work, 0, xorInto: false, m, skew);
        counter?.Step();
        int lastCount = k % m;
        if (m < k)
        {
            int dataIndex = 0;
            for (int i = m; i + m <= k; i += m)
            {
                dataIndex += m;
                skew += m;
                // work[m..2m) = IFFT(next group); work[0..m) ^= work[m..2m)
                IfftDitEncoder(ops, originals, dataIndex, m, work, m, xorInto: true, m, skew);
                counter?.Step();
            }

            if (lastCount != 0)
            {
                dataIndex += m;
                skew += m;
                IfftDitEncoder(ops, originals, dataIndex, lastCount, work, m, xorInto: true, m, skew);
                counter?.Step();
            }
        }

        // recovery = FFT(work) truncated to recoveryCount outputs.
        FftDit(ops, work, 0, recoveryCount, m, -1);
        counter?.Step();
    }

    private static void IfftDitEncoder(RegionOps ops, nint[] data, int dataIndex, int mTruncated, nint[] work, int workIndex, bool xorInto, int m, int skew)
    {
        // The originals are copied in (the rest is zero) block by block, just before the IFFT's first layers use them.
        void Prepare(int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                int local = i - workIndex;
                if (local < mTruncated)
                {
                    ops.Copy(work[i], data[dataIndex + local]);
                }
                else
                {
                    ops.Zero(work[i]);
                }
            }
        }

        IfftDitCore(ops, work, workIndex, mTruncated, m, skew, Prepare);
        if (xorInto)
        {
            for (int i = 0; i < m; i++)
            {
                ops.Xor(work[i], work[workIndex + i]);
            }
        }
    }

    /// <summary>Decimation-in-time IFFT, two layers at a time (4-way butterflies), in cache-sized blocks (see <see cref="Transform"/>).</summary>
    /// <remarks>
    /// <para><b>The "multiply by 0" shortcut.</b> A butterfly whose twiddle logarithm equals
    /// <see cref="LeopardField.Modulus"/> multiplies by zero, so it reduces to a plain XOR. <c>FftSkew[j]</c> is Modulus
    /// exactly when <c>j + 1</c> is a power of two (j = 0, 1, 3, 7, ...). Which of the twiddles can hit such an
    /// index follows from where they are read, <c>FftSkew[skew + r + c·dist]</c> with <c>r = t·4·dist</c> and
    /// <c>c = 1, 2, 3</c> for logM01, logM02 and logM23:</para>
    /// <list type="bullet">
    /// <item>with <c>skew = −1</c> (every FFT, and the IFFT of the decoder) the index plus one is
    /// <c>dist·(4t + c)</c>: a power of two for c = 1 or 2 when t = 0, but never for c = 3, because 4t + 3 is odd
    /// and larger than 1;</item>
    /// <item>with the encoder's <c>skew = (g + 1)·m − 1</c> it is <c>(g + 1)·m + r + c·dist</c>, which lies strictly
    /// between two multiples of m and so is never a power of two.</item>
    /// </list>
    /// <para>So logM01 and logM02 need the shortcut, logM23 never does. Example with dist = 1 in the decoder:
    /// logM01 = FftSkew[0], logM02 = FftSkew[1] (both Modulus at r = 0), logM23 = FftSkew[2] (not Modulus).</para>
    /// </remarks>
    private static void IfftDitCore(RegionOps ops, nint[] work, int baseIndex, int mTruncated, int m, int skew, Action<int, int>? prepare = null)
        => Transform(ops, work, baseIndex, mTruncated, m, skew, inverse: true, prepare);

    /// <summary>
    /// Four IFFT butterflies on four buffers (two layers), a piece of <see cref="ButterflyChunk"/> bytes at a time so
    /// that all four work on data in the L1 cache: each buffer goes through memory once instead of four times. A
    /// null table is a multiplication by zero (the butterfly is just the XOR).
    /// </summary>
    private static void IfftDit4(RegionOps ops, nint[] work, int i, int dist, byte* t01, byte* t23, byte* t02)
    {
        nint w0 = work[i], w1 = work[i + dist], w2 = work[i + dist * 2], w3 = work[i + dist * 3];
        for (long o = 0; o < ops.Length; o += ButterflyChunk)
        {
            var c = ops.Part(o, Math.Min(ButterflyChunk, ops.Length - o));
            c.IfftButterfly(w0, w1, t01);
            c.IfftButterfly(w2, w3, t23);
            c.IfftButterfly(w0, w2, t02);
            c.IfftButterfly(w1, w3, t02);
        }
    }

    /// <summary>Decimation-in-time FFT, two layers at a time; only the first <paramref name="mTruncated"/> outputs are needed.</summary>
    private static void FftDit(RegionOps ops, nint[] work, int baseIndex, int mTruncated, int m, int skew)
        => Transform(ops, work, baseIndex, mTruncated, m, skew, inverse: false);

    /// <summary>One layer of a transform: 4-way butterflies (two layers in one) or, at the end, 2-way ones.</summary>
    private readonly record struct Layer(int Dist, int Span, bool Radix2);

    /// <summary>The layers of the IFFT (distances 1, 4, 16, …) or the FFT (…, 16, 4, 1), in the order they run.</summary>
    private static List<Layer> Layers(int m, bool inverse)
    {
        var layers = new List<Layer>();
        if (inverse)
        {
            int dist = 1, dist4 = 4;
            for (; dist4 <= m; dist = dist4, dist4 <<= 2)
            {
                layers.Add(new(dist, dist4, false));
            }

            if (dist < m)
            {
                layers.Add(new(dist, 2 * dist, true));
            }
        }
        else
        {
            int dist4 = m, dist = m >> 2;
            for (; dist != 0; dist4 = dist, dist >>= 2)
            {
                layers.Add(new(dist, dist4, false));
            }

            if (dist4 == 2)
            {
                layers.Add(new(1, 2, true));
            }
        }

        return layers;
    }

    /// <summary>Buffers a block of layers works on at once (a column, see <see cref="Transform"/>): 256 × 1 KiB, half a Zen 3 L2.</summary>
    private const int ColumnLimit = 256;

    /// <summary>
    /// The IFFT (<paramref name="inverse"/>) or FFT of work[baseIndex .. baseIndex + m), only groups below
    /// <paramref name="mTruncated"/> computed, in blocks of layers that keep their data in the CPU cache.
    /// </summary>
    /// <remarks>
    /// <para><b>Why blocks.</b> Done layer after layer, every layer pair streams all m buffers through main memory: for
    /// a set of 5,316 blocks (16,384 positions) that is 7 sweeps per transform, and the processor mostly waits for
    /// memory (on 4 threads as slow as on 1). A layer with distance d only combines buffers that are d apart, so
    /// consecutive layers with distances lo … hi only mix buffers in one <i>column</i>: the same position modulo lo,
    /// within the same group of the largest span G. Doing all layers of such a block for one column at a time keeps
    /// the column (G ÷ lo ≤ 256 buffers of 1 KiB, see <see cref="PartBytes"/>) in the core's L2 cache:</para>
    /// <code>
    /// IFFT, m = 16384:  layers 1 4 16 64 | 256 1024 4096     (two sweeps instead of seven)
    /// block "256 1024 4096": G = 16384, lo = 256: column c = buffers c + 256·j, j = 0..63
    /// </code>
    /// <para>Measured on 16 cores, 5,316 blocks (n = 16,384), slices of 64 KiB: a decode pass took 760 ms layer by
    /// layer, 360 ms in blocks of 256 buffers, 320 ms with the derivative and the multiply-in also done block by block
    /// (see <see cref="FormalDerivative"/>).</para>
    /// <para>The order of the butterflies within a column is the layer order, and different columns share nothing,
    /// so the result is the same as layer by layer. Each group's twiddle tables are built once per block.</para>
    /// </remarks>
    /// <param name="prepare">Called with (first, count) for each group of consecutive buffers the first block of layers
    /// is about to use, to fill them just before (IFFT only: its first block works on consecutive buffers).</param>
    private static void Transform(RegionOps ops, nint[] work, int baseIndex, int mTruncated, int m, int skew, bool inverse, Action<int, int>? prepare = null)
    {
        var f = ops.Field;
        var layers = Layers(m, inverse);
        int stride = MaxTable;
        for (int first = 0; first < layers.Count;)
        {
            // The block: as many layers as keep a column within ColumnLimit buffers.
            int last = first;
            int lo = layers[first].Dist, span = layers[first].Span;
            while (last + 1 < layers.Count)
            {
                var next = layers[last + 1];
                if (Math.Max(span, next.Span) / Math.Min(lo, next.Dist) > ColumnLimit)
                {
                    break;
                }

                last++;
                lo = Math.Min(lo, next.Dist);
                span = Math.Max(span, next.Span);
            }

            int groups = 0;
            for (int l = first; l <= last; l++)
            {
                groups += span / layers[l].Span;
            }

            byte* tableData = (byte*)NativeMemory.Alloc((nuint)(groups * 3 * stride));
            var tables = new nint[groups * 3];
            try
            {
                for (int s = 0; s < m; s += span)
                {
                    if (first == 0 && prepare != null)
                    {
                        Debug.Assert(lo == 1, "the first block of an IFFT works on consecutive buffers");
                        prepare(baseIndex + s, span);
                    }

                    // The twiddle tables of every group of the block in this super-group, built once.
                    int g = 0;
                    for (int l = first; l <= last; l++)
                    {
                        var layer = layers[l];
                        for (int r = s; r < s + span; r += layer.Span, g++)
                        {
                            if (r >= mTruncated)
                            {
                                continue;
                            }

                            byte* t = tableData + (long)g * 3 * stride;
                            tables[g * 3] = (nint)ops.Table(f.FftSkew[skew + r + layer.Dist], t);
                            if (!layer.Radix2)
                            {
                                tables[g * 3 + 1] = (nint)ops.Table(f.FftSkew[skew + r + layer.Dist * 2], t + stride);
                                tables[g * 3 + 2] = (nint)ops.Table(f.FftSkew[skew + r + layer.Dist * 3], t + 2 * stride);
                                Debug.Assert(tables[g * 3 + 2] != 0, "logM23 can never be Modulus");
                            }
                        }
                    }

                    for (int c = 0; c < lo; c++)
                    {
                        g = 0;
                        for (int l = first; l <= last; l++)
                        {
                            var layer = layers[l];
                            for (int r = s; r < s + span; r += layer.Span, g++)
                            {
                                if (r >= mTruncated)
                                {
                                    continue;
                                }

                                byte* t01 = (byte*)tables[g * 3];
                                for (int i = r + c; i < r + layer.Dist; i += lo)
                                {
                                    int at = baseIndex + i;
                                    if (layer.Radix2 && inverse)
                                    {
                                        ops.IfftButterfly(work[at], work[at + layer.Dist], t01);
                                    }
                                    else if (layer.Radix2)
                                    {
                                        ops.FftButterfly(work[at], work[at + 1], t01);
                                    }
                                    else if (inverse)
                                    {
                                        IfftDit4(ops, work, at, layer.Dist, t01, (byte*)tables[g * 3 + 2], (byte*)tables[g * 3 + 1]);
                                    }
                                    else
                                    {
                                        FftDit4(ops, work, at, layer.Dist, t01, (byte*)tables[g * 3 + 2], (byte*)tables[g * 3 + 1]);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                NativeMemory.Free(tableData);
            }

            first = last + 1;
        }
    }

    /// <summary>Four FFT butterflies on four buffers, in pieces that stay in the L1 cache (see <see cref="IfftDit4"/>).</summary>
    private static void FftDit4(RegionOps ops, nint[] work, int i, int dist, byte* t01, byte* t23, byte* t02)
    {
        nint w0 = work[i], w1 = work[i + dist], w2 = work[i + dist * 2], w3 = work[i + dist * 3];
        for (long o = 0; o < ops.Length; o += ButterflyChunk)
        {
            var c = ops.Part(o, Math.Min(ButterflyChunk, ops.Length - o));
            c.FftButterfly(w0, w2, t02);
            c.FftButterfly(w1, w3, t02);
            c.FftButterfly(w0, w1, t01);
            c.FftButterfly(w2, w3, t23);
        }
    }

    // ------------------------------------------------------------------ decode

    private static void DecodeRange(LeopardField field, nint[] originals, nint[] recovery, nint[] work, int m, int n, int[] err, long ofs, long len, StepCounter? counter)
    {
        var ops = RegionOps.For(field, ofs, len);
        int k = originals.Length;
        int rc = recovery.Length;

        // Multiply received blocks by their error-locator values (the rest is zero): done for each block of buffers
        // just before the IFFT's first layers use it, so the buffers go through memory once less.
        void Prepare(int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                nint source = i < rc ? recovery[i] : i >= m && i < m + k ? originals[i - m] : 0;
                if (source != 0)
                {
                    ops.Mul(work[i], source, err[i]);
                }
                else
                {
                    ops.Zero(work[i]);
                }
            }
        }

        IfftDitCore(ops, work, 0, m + k, n, -1, Prepare);
        counter?.Step(9);

        FormalDerivative(ops, work, n);
        counter?.Step(1);
        FftDit(ops, work, 0, m + k, n, -1);
        counter?.Step(9);

        // Reveal erasures: divide by the locator value.
        for (int i = 0; i < k; i++)
        {
            if (originals[i] == 0)
            {
                ops.Mul(work[i], work[i + m], field.Modulus - err[i + m]);
            }
        }

        counter?.Step(1);
    }

    /// <summary>Consecutive buffers the formal derivative works through at once (they stay in the cache).</summary>
    private const int DerivativeBlock = 256;

    /// <summary>
    /// The formal derivative in the novel polynomial basis, a fixed XOR pattern:
    /// <c>out[t] = in[t] ⊕ in[t + 2^b]</c> for every bit b that is 0 in t.
    /// </summary>
    /// <remarks>
    /// <para>Leopard writes it as a loop (for i = 1 … n−1, w = the lowest set bit of i: work[i − w .. i) ^= work[i .. i + w)),
    /// which goes through all n buffers once per bit, log2 n times. Its XORs cancel down to the formula above
    /// (checked against the loop for every n up to 16,384). Example, n = 8: out[0] = in[0] ⊕ in[1] ⊕ in[2] ⊕ in[4],
    /// out[5] = in[5] ⊕ in[7], out[7] = in[7].</para>
    /// <para>In ascending order every input read is still unchanged, so it can be done in place, and block by block
    /// (<see cref="DerivativeBlock"/> buffers): first the bits inside the block, from the block itself, then the
    /// higher bits, from the blocks above it.</para>
    /// </remarks>
    private static void FormalDerivative(RegionOps ops, nint[] work, int n)
    {
        int block = Math.Min(n, DerivativeBlock);
        int blocks = n / block;
        for (int start = 0; start < n; start += block)
        {
            for (int lo = 0; lo < block; lo++)
            {
                for (int bit = 1; bit < block; bit <<= 1)
                {
                    if ((lo & bit) == 0)
                    {
                        ops.Xor(work[start + lo], work[start + lo + bit]);
                    }
                }
            }

            int hi = start / block;
            for (int bit = 1; bit < blocks; bit <<= 1)
            {
                if ((hi & bit) == 0)
                {
                    int source = start + bit * block;
                    for (int lo = 0; lo < block; lo++)
                    {
                        ops.Xor(work[start + lo], work[source + lo]);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ region helpers

    private static void Copy(byte* dst, byte* src, long len) => Buffer.MemoryCopy(src, dst, len, len);

    /// <summary>dst ^= src.</summary>
    private static void Xor(byte* dst, byte* src, long len)
    {
        long i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= len; i += 32)
            {
                (Vector256.Load(dst + i) ^ Vector256.Load(src + i)).Store(dst + i);
            }
        }

        for (; i + 16 <= len; i += 16)
        {
            (Vector128.Load(dst + i) ^ Vector128.Load(src + i)).Store(dst + i);
        }

        for (; i < len; i++)
        {
            dst[i] ^= src[i];
        }
    }

    /// <summary>Bytes of a butterfly group done at once: the pieces of its four buffers (16 KiB) stay in the L1 cache.</summary>
    private const long ButterflyChunk = 4096;

    /// <summary>The largest multiplication table of any kernel (GF(2^16), scalar: 1 KiB).</summary>
    private const int MaxTable = 1024;

    /// <summary>
    /// Region operations restricted to the byte range [ofs, ofs + len) of every buffer, with the kernel
    /// (<paramref name="level"/>) fixed, so tables built here fit the multiplications done here.
    /// </summary>
    private readonly struct RegionOps(LeopardField gf, long ofs, long len, SimdLevel level)
    {
        public static RegionOps For(LeopardField field, long ofs, long len)
            => new(field, ofs, len, field.Bits == 16 ? Gf16Kernels.Level : Gf8Kernels.Level);

        public LeopardField Field => gf;

        public long Length => len;

        /// <summary>The bytes [offset, offset + length) of this range.</summary>
        public RegionOps Part(long offset, long length) => new(gf, ofs + offset, length, level);

        private Span<byte> S(nint p) => new((byte*)p + ofs, (int)len);

        public void Copy(nint dst, nint src) => LeopardCodec.Copy((byte*)dst + ofs, (byte*)src + ofs, len);

        public void Zero(nint dst) => S(dst).Clear();

        /// <summary>dst ^= src.</summary>
        public void Xor(nint dst, nint src) => LeopardCodec.Xor((byte*)dst + ofs, (byte*)src + ofs, len);

        /// <summary>x = y · exp(logM).</summary>
        public void Mul(nint x, nint y, int logM)
        {
            int c = Field.Exp(logM);
            if (Field.Bits == 16)
            {
                Gf16Kernels.MulAdd(Field, (ushort)c, S(y), S(x), overwrite: true);
            }
            else
            {
                Gf8Kernels.MulAdd(Field, (byte)c, S(y), S(x), overwrite: true);
            }
        }

        /// <summary>
        /// Builds the table of exp(<paramref name="logM"/>) into <paramref name="buffer"/> and returns it; null for
        /// logM = Modulus, a multiplication by zero.
        /// </summary>
        public byte* Table(int logM, byte* buffer)
        {
            if (logM == Field.Modulus)
            {
                return null;
            }

            int c = Field.Exp(logM);
            if (Field.Bits == 16)
            {
                Gf16Kernels.BuildTableFast(Field, level, (ushort)c, buffer);
            }
            else
            {
                Gf8Kernels.BuildTableFast(Field, level, (byte)c, buffer);
            }

            return buffer;
        }

        /// <summary>IFFT butterfly: y ^= x; x ^= y · c (just the XOR for a null table, c = 0).</summary>
        public void IfftButterfly(nint x, nint y, byte* table)
        {
            for (long o = 0; o < len; o += ButterflyChunk)
            {
                var p = Part(o, Math.Min(ButterflyChunk, len - o));
                p.Xor(y, x);
                if (table != null)
                {
                    p.MulAdd(x, y, table);
                }
            }
        }

        /// <summary>FFT butterfly: x ^= y · c; y ^= x (just the XOR for a null table, c = 0).</summary>
        public void FftButterfly(nint x, nint y, byte* table)
        {
            for (long o = 0; o < len; o += ButterflyChunk)
            {
                var p = Part(o, Math.Min(ButterflyChunk, len - o));
                if (table != null)
                {
                    p.MulAdd(x, y, table);
                }

                p.Xor(y, x);
            }
        }

        /// <summary>x ^= y · c, c given by its table.</summary>
        private void MulAdd(nint x, nint y, byte* table)
        {
            if (Field.Bits == 16)
            {
                Gf16Kernels.MulAddWithTable(level, table, (byte*)y + ofs, (byte*)x + ofs, (nint)len, overwrite: false);
            }
            else
            {
                Gf8Kernels.MulAddWithTable(Field, level, table, (byte*)y + ofs, (byte*)x + ofs, (nint)len, overwrite: false);
            }
        }
    }
}

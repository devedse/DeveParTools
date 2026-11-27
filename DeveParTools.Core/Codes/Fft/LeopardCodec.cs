using System.Diagnostics;
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

    /// <summary>How many parts <see cref="RunParallel"/> splits <paramref name="bytes"/> into, and how long each is.</summary>
    private static (int Count, long Per) Ranges(long bytes, int threads)
    {
        // Independent ranges: a multiple of 64 bytes (and of the symbol size).
        long chunks = Math.Max(1, Math.Min(Math.Max(1, threads), bytes / 4096));
        long per = (bytes + chunks - 1) / chunks;
        per = (per + 63) / 64 * 64;
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
        for (int i = 0; i < mTruncated; i++)
        {
            ops.Copy(work[workIndex + i], data[dataIndex + i]);
        }

        for (int i = mTruncated; i < m; i++)
        {
            ops.Zero(work[workIndex + i]);
        }

        IfftDitCore(ops, work, workIndex, mTruncated, m, skew);
        if (xorInto)
        {
            for (int i = 0; i < m; i++)
            {
                ops.Xor(work[i], work[workIndex + i]);
            }
        }
    }

    /// <summary>Decimation-in-time IFFT, two layers at a time (4-way butterflies).</summary>
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
    private static void IfftDitCore(RegionOps ops, nint[] work, int baseIndex, int mTruncated, int m, int skew)
    {
        var f = ops.Field;
        byte* t01 = stackalloc byte[MaxTable];
        byte* t23 = stackalloc byte[MaxTable];
        byte* t02 = stackalloc byte[MaxTable];
        int dist = 1, dist4 = 4;
        for (; dist4 <= m; dist = dist4, dist4 <<= 2)
        {
            for (int r = 0; r < mTruncated; r += dist4)
            {
                // The twiddles are the same for the whole group: their tables are built once.
                int iEnd = r + dist;
                byte* p01 = ops.Table(f.FftSkew[skew + iEnd], t01);
                byte* p02 = ops.Table(f.FftSkew[skew + iEnd + dist], t02);
                byte* p23 = ops.Table(f.FftSkew[skew + iEnd + dist * 2], t23);
                Debug.Assert(p23 != null, "logM23 can never be Modulus");
                for (int i = r; i < iEnd; i++)
                {
                    IfftDit4(ops, work, baseIndex + i, dist, p01, p23, p02);
                }
            }
        }

        if (dist < m)
        {
            byte* p = ops.Table(f.FftSkew[skew + dist], t01);
            for (int i = 0; i < dist; i++)
            {
                ops.IfftButterfly(work[baseIndex + i], work[baseIndex + i + dist], p);
            }
        }
    }

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
    {
        var f = ops.Field;
        byte* t01 = stackalloc byte[MaxTable];
        byte* t23 = stackalloc byte[MaxTable];
        byte* t02 = stackalloc byte[MaxTable];
        int dist4 = m, dist = m >> 2;
        for (; dist != 0; dist4 = dist, dist >>= 2)
        {
            for (int r = 0; r < mTruncated; r += dist4)
            {
                int iEnd = r + dist;
                byte* p01 = ops.Table(f.FftSkew[skew + iEnd], t01);
                byte* p02 = ops.Table(f.FftSkew[skew + iEnd + dist], t02);
                byte* p23 = ops.Table(f.FftSkew[skew + iEnd + dist * 2], t23);
                Debug.Assert(p23 != null, "logM23 can never be Modulus");
                for (int i = r; i < iEnd; i++)
                {
                    FftDit4(ops, work, baseIndex + i, dist, p01, p23, p02);
                }
            }
        }

        if (dist4 == 2)
        {
            for (int r = 0; r < mTruncated; r += 2)
            {
                ops.FftButterfly(work[baseIndex + r], work[baseIndex + r + 1], ops.Table(f.FftSkew[skew + r + 1], t01));
            }
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

        // Multiply received blocks by their error-locator values.
        for (int i = 0; i < rc; i++)
        {
            if (recovery[i] != 0)
            {
                ops.Mul(work[i], recovery[i], err[i]);
            }
            else
            {
                ops.Zero(work[i]);
            }
        }

        for (int i = rc; i < m; i++)
        {
            ops.Zero(work[i]);
        }

        for (int i = 0; i < k; i++)
        {
            if (originals[i] != 0)
            {
                ops.Mul(work[m + i], originals[i], err[m + i]);
            }
            else
            {
                ops.Zero(work[m + i]);
            }
        }

        for (int i = m + k; i < n; i++)
        {
            ops.Zero(work[i]);
        }

        IfftDitCore(ops, work, 0, m + k, n, -1);
        counter?.Step(9);

        // Formal derivative: in the novel polynomial basis this is a fixed XOR pattern.
        for (int i = 1; i < n; i++)
        {
            int width = ((i ^ (i - 1)) + 1) >> 1;
            for (int j = 0; j < width; j++)
            {
                ops.Xor(work[i - width + j], work[i + j]);
            }
        }

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

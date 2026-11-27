using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DeveParTools.Core.Hashing;

/// <summary>
/// Hashes many independent inputs at once (whole chunks, or parent blocks), one input per SIMD lane.
/// </summary>
/// <remarks>
/// <para><b>Lanes.</b> The scalar compression keeps its 16 state words in 16 registers. The SIMD version keeps 16
/// <i>vectors</i> instead: vector <c>s5</c> holds state word 5 of chunk 0 in lane 0, of chunk 1 in lane 1, and so on.
/// Every scalar instruction becomes one vector instruction, so 8 chunks (AVX2) cost about as much as one.</para>
///
/// <para><b>Transposition.</b> The message words must be in the same "one chunk per lane" layout: vector
/// <c>m[w]</c> must hold word <c>w</c> of every chunk. In memory each chunk's block is contiguous, so a plain load gives
/// the words of <i>one</i> chunk. Loading one row per chunk and transposing the matrix gives the wanted layout.
/// With 4 lanes and words a..d of chunks 0..3:</para>
/// <code>
///   loaded (one row per chunk)        transposed (one row per word = m[0..3])
///   chunk0: a0 b0 c0 d0               m[0]: a0 a1 a2 a3
///   chunk1: a1 b1 c1 d1      ──►      m[1]: b0 b1 b2 b3
///   chunk2: a2 b2 c2 d2               m[2]: c0 c1 c2 c3
///   chunk3: a3 b3 c3 d3               m[3]: d0 d1 d2 d3
/// </code>
/// <para>The transposition uses "unpack" (interleave) instructions in two steps:</para>
/// <code>
///   t0 = unpacklo32(chunk0, chunk1) = a0 a1 b0 b1      t1 = unpackhi32(chunk0, chunk1) = c0 c1 d0 d1
///   t2 = unpacklo32(chunk2, chunk3) = a2 a3 b2 b3      t3 = unpackhi32(chunk2, chunk3) = c2 c3 d2 d3
///   m0 = unpacklo64(t0, t2) = a0 a1 a2 a3              m1 = unpackhi64(t0, t2) = b0 b1 b2 b3
///   m2 = unpacklo64(t1, t3) = c0 c1 c2 c3              m3 = unpackhi64(t1, t3) = d0 d1 d2 d3
/// </code>
/// <para>AVX2 and AVX-512 unpack inside each 128-bit lane only. So the 8×8 and 16×16 versions do the same two steps
/// and then move whole 128-bit lanes into place (<c>vperm2i128</c> or <c>vshufi32x4</c>, see each method).</para>
///
/// <para><b>Rotations.</b> G rotates right by 16, 12, 8 and 7. AVX-512 has a rotate instruction. Without it, rotating by
/// 16 or 8 is a byte shuffle (one <c>pshufb</c>, or <c>rev32</c>/<c>tbl</c> on ARM). Rotating by 12 or 7 needs two
/// shifts and an OR, or one shift plus <c>sli</c> on ARM.</para>
/// </remarks>
internal static unsafe class Blake3Simd
{
    /// <summary>The widest lane count to use (16, 8, 4 or 1). Tests lower it to run every code path.</summary>
    internal static int MaxLanes { get; set; } = 16;

    /// <summary>
    /// Hashes <paramref name="count"/> inputs, each <paramref name="blocks"/> blocks long, that start
    /// <paramref name="stride"/> bytes apart. Writes one 8-word chaining value per input.
    /// </summary>
    /// <param name="counter">Counter of the first input. With <paramref name="incrementCounter"/>, input i uses counter + i (chunks); otherwise all use the same counter (parents use 0).</param>
    /// <param name="flags">Flags for every block (PARENT for parents).</param>
    /// <param name="flagsStart">Extra flags for the first block of each input (CHUNK_START for chunks).</param>
    /// <param name="flagsEnd">Extra flags for the last block of each input (CHUNK_END for chunks).</param>
    public static void HashMany(byte* input, int count, int stride, int blocks, ulong counter, bool incrementCounter, uint flags, uint flagsStart, uint flagsEnd, uint* cvOut)
    {
        int i = 0;
        if (BitConverter.IsLittleEndian)
        {
            if (MaxLanes >= 16 && Lanes16.IsSupported)
            {
                for (; i + 16 <= count; i += 16)
                {
                    HashLanes<Vector512<uint>, Lanes16>(input + (long)i * stride, stride, blocks, counter + (incrementCounter ? (ulong)i : 0), incrementCounter, flags, flagsStart, flagsEnd, cvOut + i * 8);
                }
            }

            if (MaxLanes >= 8 && Lanes8.IsSupported)
            {
                for (; i + 8 <= count; i += 8)
                {
                    HashLanes<Vector256<uint>, Lanes8>(input + (long)i * stride, stride, blocks, counter + (incrementCounter ? (ulong)i : 0), incrementCounter, flags, flagsStart, flagsEnd, cvOut + i * 8);
                }
            }

            if (MaxLanes >= 4 && Lanes4.IsSupported)
            {
                for (; i + 4 <= count; i += 4)
                {
                    HashLanes<Vector128<uint>, Lanes4>(input + (long)i * stride, stride, blocks, counter + (incrementCounter ? (ulong)i : 0), incrementCounter, flags, flagsStart, flagsEnd, cvOut + i * 8);
                }
            }
        }

        for (; i < count; i++)
        {
            HashOne(input + (long)i * stride, blocks, counter + (incrementCounter ? (ulong)i : 0), flags, flagsStart, flagsEnd, cvOut + i * 8);
        }
    }

    [SkipLocalsInit]
    private static void HashOne(byte* input, int blocks, ulong counter, uint flags, uint flagsStart, uint flagsEnd, uint* cvOut)
    {
        // Work on a local CV: the output may overlap the input (in-place parent reduction).
        Span<uint> cv = stackalloc uint[8];
        Span<uint> m = stackalloc uint[16];
        Span<uint> o = stackalloc uint[16];
        Blake3Compression.LoadIV(cv);
        for (int b = 0; b < blocks; b++)
        {
            Blake3Compression.LoadWords(new ReadOnlySpan<byte>(input + b * Blake3.BlockLen, Blake3.BlockLen), m);
            uint f = flags | (b == 0 ? flagsStart : 0) | (b == blocks - 1 ? flagsEnd : 0);
            Blake3Compression.Compress(cv, m, counter, Blake3.BlockLen, f, o);
            o[..8].CopyTo(cv);
        }

        cv.CopyTo(new Span<uint>(cvOut, 8));
    }

    [SkipLocalsInit]
    private static void HashLanes<TV, TL>(byte* input, int stride, int blocks, ulong counter, bool incrementCounter, uint flags, uint flagsStart, uint flagsEnd, uint* cvOut)
        where TV : unmanaged
        where TL : struct, IBlake3Lanes<TV>
    {
        int lanes = TL.Lanes;
        TV* h = stackalloc TV[8];
        TV* m = stackalloc TV[16];
        uint* scratch = stackalloc uint[8 * 16];
        for (int i = 0; i < lanes; i++)
        {
            ulong c = counter + (incrementCounter ? (ulong)i : 0);
            scratch[i] = (uint)c;
            scratch[16 + i] = (uint)(c >> 32);
        }

        TV counterLow = TL.Load(scratch);
        TV counterHigh = TL.Load(scratch + 16);
        h[0] = TL.Broadcast(Blake3.IV0);
        h[1] = TL.Broadcast(Blake3.IV1);
        h[2] = TL.Broadcast(Blake3.IV2);
        h[3] = TL.Broadcast(Blake3.IV3);
        h[4] = TL.Broadcast(Blake3.IV4);
        h[5] = TL.Broadcast(Blake3.IV5);
        h[6] = TL.Broadcast(Blake3.IV6);
        h[7] = TL.Broadcast(Blake3.IV7);
        TV blockLen = TL.Broadcast(Blake3.BlockLen);
        for (int b = 0; b < blocks; b++)
        {
            TL.LoadTransposed(input + b * Blake3.BlockLen, stride, m);
            uint f = flags | (b == 0 ? flagsStart : 0) | (b == blocks - 1 ? flagsEnd : 0);
            Compress<TV, TL>(h, m, counterLow, counterHigh, blockLen, TL.Broadcast(f));
        }

        // Back from "one word per vector" to "one chaining value per input" (a small scalar transpose).
        for (int w = 0; w < 8; w++)
        {
            TL.Store(scratch + w * lanes, h[w]);
        }

        for (int lane = 0; lane < lanes; lane++)
        {
            for (int w = 0; w < 8; w++)
            {
                cvOut[lane * 8 + w] = scratch[w * lanes + lane];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G<TV, TL>(ref TV a, ref TV b, ref TV c, ref TV d, TV mx, TV my)
        where TV : unmanaged
        where TL : struct, IBlake3Lanes<TV>
    {
        a = TL.Add(TL.Add(a, b), mx);
        d = TL.Ror16(TL.Xor(d, a));
        c = TL.Add(c, d);
        b = TL.Ror12(TL.Xor(b, c));
        a = TL.Add(TL.Add(a, b), my);
        d = TL.Ror8(TL.Xor(d, a));
        c = TL.Add(c, d);
        b = TL.Ror7(TL.Xor(b, c));
    }

    /// <summary>The compression function on every lane at once; same rounds as <see cref="Blake3Compression.Compress"/>.</summary>
    private static void Compress<TV, TL>(TV* h, TV* m, TV counterLow, TV counterHigh, TV blockLen, TV flags)
        where TV : unmanaged
        where TL : struct, IBlake3Lanes<TV>
    {
        TV s0 = h[0], s1 = h[1], s2 = h[2], s3 = h[3], s4 = h[4], s5 = h[5], s6 = h[6], s7 = h[7];
        TV s8 = TL.Broadcast(Blake3.IV0), s9 = TL.Broadcast(Blake3.IV1), s10 = TL.Broadcast(Blake3.IV2), s11 = TL.Broadcast(Blake3.IV3);
        TV s12 = counterLow, s13 = counterHigh, s14 = blockLen, s15 = flags;

        // Round 0: message words 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        G<TV, TL>(ref s0, ref s4, ref s8, ref s12, m[0], m[1]);
        G<TV, TL>(ref s1, ref s5, ref s9, ref s13, m[2], m[3]);
        G<TV, TL>(ref s2, ref s6, ref s10, ref s14, m[4], m[5]);
        G<TV, TL>(ref s3, ref s7, ref s11, ref s15, m[6], m[7]);
        G<TV, TL>(ref s0, ref s5, ref s10, ref s15, m[8], m[9]);
        G<TV, TL>(ref s1, ref s6, ref s11, ref s12, m[10], m[11]);
        G<TV, TL>(ref s2, ref s7, ref s8, ref s13, m[12], m[13]);
        G<TV, TL>(ref s3, ref s4, ref s9, ref s14, m[14], m[15]);
        // Round 1: message words 2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8
        G<TV, TL>(ref s0, ref s4, ref s8, ref s12, m[2], m[6]);
        G<TV, TL>(ref s1, ref s5, ref s9, ref s13, m[3], m[10]);
        G<TV, TL>(ref s2, ref s6, ref s10, ref s14, m[7], m[0]);
        G<TV, TL>(ref s3, ref s7, ref s11, ref s15, m[4], m[13]);
        G<TV, TL>(ref s0, ref s5, ref s10, ref s15, m[1], m[11]);
        G<TV, TL>(ref s1, ref s6, ref s11, ref s12, m[12], m[5]);
        G<TV, TL>(ref s2, ref s7, ref s8, ref s13, m[9], m[14]);
        G<TV, TL>(ref s3, ref s4, ref s9, ref s14, m[15], m[8]);
        // Round 2: message words 3, 4, 10, 12, 13, 2, 7, 14, 6, 5, 9, 0, 11, 15, 8, 1
        G<TV, TL>(ref s0, ref s4, ref s8, ref s12, m[3], m[4]);
        G<TV, TL>(ref s1, ref s5, ref s9, ref s13, m[10], m[12]);
        G<TV, TL>(ref s2, ref s6, ref s10, ref s14, m[13], m[2]);
        G<TV, TL>(ref s3, ref s7, ref s11, ref s15, m[7], m[14]);
        G<TV, TL>(ref s0, ref s5, ref s10, ref s15, m[6], m[5]);
        G<TV, TL>(ref s1, ref s6, ref s11, ref s12, m[9], m[0]);
        G<TV, TL>(ref s2, ref s7, ref s8, ref s13, m[11], m[15]);
        G<TV, TL>(ref s3, ref s4, ref s9, ref s14, m[8], m[1]);
        // Round 3: message words 10, 7, 12, 9, 14, 3, 13, 15, 4, 0, 11, 2, 5, 8, 1, 6
        G<TV, TL>(ref s0, ref s4, ref s8, ref s12, m[10], m[7]);
        G<TV, TL>(ref s1, ref s5, ref s9, ref s13, m[12], m[9]);
        G<TV, TL>(ref s2, ref s6, ref s10, ref s14, m[14], m[3]);
        G<TV, TL>(ref s3, ref s7, ref s11, ref s15, m[13], m[15]);
        G<TV, TL>(ref s0, ref s5, ref s10, ref s15, m[4], m[0]);
        G<TV, TL>(ref s1, ref s6, ref s11, ref s12, m[11], m[2]);
        G<TV, TL>(ref s2, ref s7, ref s8, ref s13, m[5], m[8]);
        G<TV, TL>(ref s3, ref s4, ref s9, ref s14, m[1], m[6]);
        // Round 4: message words 12, 13, 9, 11, 15, 10, 14, 8, 7, 2, 5, 3, 0, 1, 6, 4
        G<TV, TL>(ref s0, ref s4, ref s8, ref s12, m[12], m[13]);
        G<TV, TL>(ref s1, ref s5, ref s9, ref s13, m[9], m[11]);
        G<TV, TL>(ref s2, ref s6, ref s10, ref s14, m[15], m[10]);
        G<TV, TL>(ref s3, ref s7, ref s11, ref s15, m[14], m[8]);
        G<TV, TL>(ref s0, ref s5, ref s10, ref s15, m[7], m[2]);
        G<TV, TL>(ref s1, ref s6, ref s11, ref s12, m[5], m[3]);
        G<TV, TL>(ref s2, ref s7, ref s8, ref s13, m[0], m[1]);
        G<TV, TL>(ref s3, ref s4, ref s9, ref s14, m[6], m[4]);
        // Round 5: message words 9, 14, 11, 5, 8, 12, 15, 1, 13, 3, 0, 10, 2, 6, 4, 7
        G<TV, TL>(ref s0, ref s4, ref s8, ref s12, m[9], m[14]);
        G<TV, TL>(ref s1, ref s5, ref s9, ref s13, m[11], m[5]);
        G<TV, TL>(ref s2, ref s6, ref s10, ref s14, m[8], m[12]);
        G<TV, TL>(ref s3, ref s7, ref s11, ref s15, m[15], m[1]);
        G<TV, TL>(ref s0, ref s5, ref s10, ref s15, m[13], m[3]);
        G<TV, TL>(ref s1, ref s6, ref s11, ref s12, m[0], m[10]);
        G<TV, TL>(ref s2, ref s7, ref s8, ref s13, m[2], m[6]);
        G<TV, TL>(ref s3, ref s4, ref s9, ref s14, m[4], m[7]);
        // Round 6: message words 11, 15, 5, 0, 1, 9, 8, 6, 14, 10, 2, 12, 3, 4, 7, 13
        G<TV, TL>(ref s0, ref s4, ref s8, ref s12, m[11], m[15]);
        G<TV, TL>(ref s1, ref s5, ref s9, ref s13, m[5], m[0]);
        G<TV, TL>(ref s2, ref s6, ref s10, ref s14, m[1], m[9]);
        G<TV, TL>(ref s3, ref s7, ref s11, ref s15, m[8], m[6]);
        G<TV, TL>(ref s0, ref s5, ref s10, ref s15, m[14], m[10]);
        G<TV, TL>(ref s1, ref s6, ref s11, ref s12, m[2], m[12]);
        G<TV, TL>(ref s2, ref s7, ref s8, ref s13, m[3], m[4]);
        G<TV, TL>(ref s3, ref s4, ref s9, ref s14, m[7], m[13]);

        h[0] = TL.Xor(s0, s8);
        h[1] = TL.Xor(s1, s9);
        h[2] = TL.Xor(s2, s10);
        h[3] = TL.Xor(s3, s11);
        h[4] = TL.Xor(s4, s12);
        h[5] = TL.Xor(s5, s13);
        h[6] = TL.Xor(s6, s14);
        h[7] = TL.Xor(s7, s15);
    }
}

/// <summary>The vector operations <see cref="Blake3Simd"/> needs, for one vector width.</summary>
internal unsafe interface IBlake3Lanes<TV>
    where TV : unmanaged
{
    static abstract int Lanes { get; }

    static abstract bool IsSupported { get; }

    static abstract TV Broadcast(uint value);

    static abstract TV Load(uint* source);

    static abstract void Store(uint* destination, TV value);

    static abstract TV Add(TV a, TV b);

    static abstract TV Xor(TV a, TV b);

    static abstract TV Ror16(TV x);

    static abstract TV Ror12(TV x);

    static abstract TV Ror8(TV x);

    static abstract TV Ror7(TV x);

    /// <summary>
    /// Loads the 64-byte block at <paramref name="input"/> + lane × <paramref name="stride"/> for every lane and
    /// transposes, so that <c>m[w]</c> holds message word <c>w</c> of every lane.
    /// </summary>
    static abstract void LoadTransposed(byte* input, int stride, TV* m);
}

/// <summary>4 lanes: SSE2/SSSE3 on x86/x64, AdvSimd (NEON) on ARM64.</summary>
internal readonly unsafe struct Lanes4 : IBlake3Lanes<Vector128<uint>>
{
    public static int Lanes => 4;

    public static bool IsSupported => Vector128.IsHardwareAccelerated;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Broadcast(uint value) => Vector128.Create(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Load(uint* source) => Vector128.Load(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(uint* destination, Vector128<uint> value) => value.Store(destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Add(Vector128<uint> a, Vector128<uint> b) => a + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Xor(Vector128<uint> a, Vector128<uint> b) => a ^ b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ror16(Vector128<uint> x)
    {
        if (Avx512F.VL.IsSupported)
        {
            return Avx512F.VL.RotateRight(x, 16);
        }

        if (Ssse3.IsSupported)
        {
            // Bytes b0 b1 b2 b3 of each word become b2 b3 b0 b1.
            return Ssse3.Shuffle(x.AsByte(), Vector128.Create((byte)2, 3, 0, 1, 6, 7, 4, 5, 10, 11, 8, 9, 14, 15, 12, 13)).AsUInt32();
        }

        if (AdvSimd.IsSupported)
        {
            // rev32 on 16-bit elements swaps the two halves of every word.
            return AdvSimd.ReverseElement16(x.AsInt32()).AsUInt32();
        }

        return Vector128.ShiftRightLogical(x, 16) | Vector128.ShiftLeft(x, 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ror12(Vector128<uint> x) => RorShift(x, 12, 20);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ror8(Vector128<uint> x)
    {
        if (Avx512F.VL.IsSupported)
        {
            return Avx512F.VL.RotateRight(x, 8);
        }

        // Bytes b0 b1 b2 b3 of each word become b1 b2 b3 b0.
        var mask = Vector128.Create((byte)1, 2, 3, 0, 5, 6, 7, 4, 9, 10, 11, 8, 13, 14, 15, 12);
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(x.AsByte(), mask).AsUInt32();
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.VectorTableLookup(x.AsByte(), mask).AsUInt32();
        }

        return Vector128.ShiftRightLogical(x, 8) | Vector128.ShiftLeft(x, 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ror7(Vector128<uint> x) => RorShift(x, 7, 25);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RorShift(Vector128<uint> x, [ConstantExpected(Min = 1, Max = 31)] byte n, [ConstantExpected(Min = 1, Max = 31)] byte complement)
    {
        if (Avx512F.VL.IsSupported)
        {
            return Avx512F.VL.RotateRight(x, n);
        }

        if (AdvSimd.IsSupported)
        {
            // sli writes x << (32-n) over the top n bits of x >> n: one instruction instead of a shift and an OR.
            return AdvSimd.ShiftLeftAndInsert(AdvSimd.ShiftRightLogical(x, n), x, complement);
        }

        return Vector128.ShiftRightLogical(x, n) | Vector128.ShiftLeft(x, complement);
    }

    public static void LoadTransposed(byte* input, int stride, Vector128<uint>* m)
    {
        // A 64-byte block is four 16-byte quarters. Quarter q of the four lanes transposes into m[4q .. 4q+3].
        for (int q = 0; q < 4; q++)
        {
            var r0 = Vector128.Load((uint*)input + q * 4);
            var r1 = Vector128.Load((uint*)(input + stride) + q * 4);
            var r2 = Vector128.Load((uint*)(input + 2 * stride) + q * 4);
            var r3 = Vector128.Load((uint*)(input + 3 * stride) + q * 4);
            Transpose(ref r0, ref r1, ref r2, ref r3);
            m[q * 4] = r0;
            m[q * 4 + 1] = r1;
            m[q * 4 + 2] = r2;
            m[q * 4 + 3] = r3;
        }
    }

    /// <summary>4×4 transpose of 32-bit words (see the class remarks of <see cref="Blake3Simd"/> for the picture).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose(ref Vector128<uint> r0, ref Vector128<uint> r1, ref Vector128<uint> r2, ref Vector128<uint> r3)
    {
        if (Sse2.IsSupported)
        {
            var t0 = Sse2.UnpackLow(r0, r1).AsUInt64();
            var t1 = Sse2.UnpackHigh(r0, r1).AsUInt64();
            var t2 = Sse2.UnpackLow(r2, r3).AsUInt64();
            var t3 = Sse2.UnpackHigh(r2, r3).AsUInt64();
            r0 = Sse2.UnpackLow(t0, t2).AsUInt32();
            r1 = Sse2.UnpackHigh(t0, t2).AsUInt32();
            r2 = Sse2.UnpackLow(t1, t3).AsUInt32();
            r3 = Sse2.UnpackHigh(t1, t3).AsUInt32();
            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            // zip1/zip2 are ARM's unpacklo/unpackhi.
            var t0 = AdvSimd.Arm64.ZipLow(r0, r1).AsUInt64();
            var t1 = AdvSimd.Arm64.ZipHigh(r0, r1).AsUInt64();
            var t2 = AdvSimd.Arm64.ZipLow(r2, r3).AsUInt64();
            var t3 = AdvSimd.Arm64.ZipHigh(r2, r3).AsUInt64();
            r0 = AdvSimd.Arm64.ZipLow(t0, t2).AsUInt32();
            r1 = AdvSimd.Arm64.ZipHigh(t0, t2).AsUInt32();
            r2 = AdvSimd.Arm64.ZipLow(t1, t3).AsUInt32();
            r3 = AdvSimd.Arm64.ZipHigh(t1, t3).AsUInt32();
            return;
        }

        var a = r0;
        var b = r1;
        var c = r2;
        var d = r3;
        r0 = Vector128.Create(a[0], b[0], c[0], d[0]);
        r1 = Vector128.Create(a[1], b[1], c[1], d[1]);
        r2 = Vector128.Create(a[2], b[2], c[2], d[2]);
        r3 = Vector128.Create(a[3], b[3], c[3], d[3]);
    }
}

/// <summary>8 lanes: AVX2.</summary>
internal readonly unsafe struct Lanes8 : IBlake3Lanes<Vector256<uint>>
{
    public static int Lanes => 8;

    public static bool IsSupported => Avx2.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Broadcast(uint value) => Vector256.Create(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Load(uint* source) => Vector256.Load(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(uint* destination, Vector256<uint> value) => value.Store(destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Add(Vector256<uint> a, Vector256<uint> b) => a + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Xor(Vector256<uint> a, Vector256<uint> b) => a ^ b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Ror16(Vector256<uint> x) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(x, 16)
        : Avx2.Shuffle(x.AsByte(), Vector256.Create((byte)2, 3, 0, 1, 6, 7, 4, 5, 10, 11, 8, 9, 14, 15, 12, 13, 2, 3, 0, 1, 6, 7, 4, 5, 10, 11, 8, 9, 14, 15, 12, 13)).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Ror12(Vector256<uint> x) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(x, 12)
        : Avx2.ShiftRightLogical(x, 12) | Avx2.ShiftLeftLogical(x, 20);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Ror8(Vector256<uint> x) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(x, 8)
        : Avx2.Shuffle(x.AsByte(), Vector256.Create((byte)1, 2, 3, 0, 5, 6, 7, 4, 9, 10, 11, 8, 13, 14, 15, 12, 1, 2, 3, 0, 5, 6, 7, 4, 9, 10, 11, 8, 13, 14, 15, 12)).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> Ror7(Vector256<uint> x) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(x, 7)
        : Avx2.ShiftRightLogical(x, 7) | Avx2.ShiftLeftLogical(x, 25);

    /// <summary>
    /// Two 8×8 transposes (words 0..7, then 8..15). After the 4×4 unpack steps, every 128-bit half holds four words
    /// of one message word, one half for lanes 0..3 and one for lanes 4..7. <c>vperm2i128</c> joins the matching halves:
    /// <code>
    ///   u0 = [ w0 of lanes 0-3 | w4 of lanes 0-3 ]      u4 = [ w0 of lanes 4-7 | w4 of lanes 4-7 ]
    ///   m[0] = perm2x128(u0, u4, 0x20) = [ w0 lanes 0-3 | w0 lanes 4-7 ]   (low halves of both)
    ///   m[4] = perm2x128(u0, u4, 0x31) = [ w4 lanes 0-3 | w4 lanes 4-7 ]   (high halves of both)
    /// </code>
    /// </summary>
    public static void LoadTransposed(byte* input, int stride, Vector256<uint>* m)
    {
        for (int half = 0; half < 2; half++)
        {
            var r0 = Avx.LoadVector256((uint*)input + half * 8);
            var r1 = Avx.LoadVector256((uint*)(input + stride) + half * 8);
            var r2 = Avx.LoadVector256((uint*)(input + 2 * stride) + half * 8);
            var r3 = Avx.LoadVector256((uint*)(input + 3 * stride) + half * 8);
            var r4 = Avx.LoadVector256((uint*)(input + 4 * stride) + half * 8);
            var r5 = Avx.LoadVector256((uint*)(input + 5 * stride) + half * 8);
            var r6 = Avx.LoadVector256((uint*)(input + 6 * stride) + half * 8);
            var r7 = Avx.LoadVector256((uint*)(input + 7 * stride) + half * 8);

            var t0 = Avx2.UnpackLow(r0, r1).AsUInt64();
            var t1 = Avx2.UnpackHigh(r0, r1).AsUInt64();
            var t2 = Avx2.UnpackLow(r2, r3).AsUInt64();
            var t3 = Avx2.UnpackHigh(r2, r3).AsUInt64();
            var t4 = Avx2.UnpackLow(r4, r5).AsUInt64();
            var t5 = Avx2.UnpackHigh(r4, r5).AsUInt64();
            var t6 = Avx2.UnpackLow(r6, r7).AsUInt64();
            var t7 = Avx2.UnpackHigh(r6, r7).AsUInt64();

            var u0 = Avx2.UnpackLow(t0, t2).AsUInt32();
            var u1 = Avx2.UnpackHigh(t0, t2).AsUInt32();
            var u2 = Avx2.UnpackLow(t1, t3).AsUInt32();
            var u3 = Avx2.UnpackHigh(t1, t3).AsUInt32();
            var u4 = Avx2.UnpackLow(t4, t6).AsUInt32();
            var u5 = Avx2.UnpackHigh(t4, t6).AsUInt32();
            var u6 = Avx2.UnpackLow(t5, t7).AsUInt32();
            var u7 = Avx2.UnpackHigh(t5, t7).AsUInt32();

            var o = m + half * 8;
            o[0] = Avx2.Permute2x128(u0, u4, 0x20);
            o[1] = Avx2.Permute2x128(u1, u5, 0x20);
            o[2] = Avx2.Permute2x128(u2, u6, 0x20);
            o[3] = Avx2.Permute2x128(u3, u7, 0x20);
            o[4] = Avx2.Permute2x128(u0, u4, 0x31);
            o[5] = Avx2.Permute2x128(u1, u5, 0x31);
            o[6] = Avx2.Permute2x128(u2, u6, 0x31);
            o[7] = Avx2.Permute2x128(u3, u7, 0x31);
        }
    }
}

/// <summary>16 lanes: AVX-512F.</summary>
internal readonly unsafe struct Lanes16 : IBlake3Lanes<Vector512<uint>>
{
    public static int Lanes => 16;

    public static bool IsSupported => Avx512F.IsSupported && Vector512.IsHardwareAccelerated;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Broadcast(uint value) => Vector512.Create(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Load(uint* source) => Vector512.Load(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(uint* destination, Vector512<uint> value) => value.Store(destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Add(Vector512<uint> a, Vector512<uint> b) => a + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Xor(Vector512<uint> a, Vector512<uint> b) => a ^ b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Ror16(Vector512<uint> x) => Avx512F.RotateRight(x, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Ror12(Vector512<uint> x) => Avx512F.RotateRight(x, 12);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Ror8(Vector512<uint> x) => Avx512F.RotateRight(x, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Ror7(Vector512<uint> x) => Avx512F.RotateRight(x, 7);

    /// <summary>
    /// 16×16 transpose: each lane's whole 64-byte block is one row. After the two unpack steps, 128-bit part L of
    /// <c>u[4k+j]</c> holds word <c>4L+j</c> of rows <c>4k..4k+3</c>. Two rounds of <c>vshufi32x4</c> collect, for each
    /// word, its four 128-bit parts (rows 0-3, 4-7, 8-11, 12-15):
    /// <code>
    ///   v0 = shuf(u[j],   u[4+j],  0x44) = [ u[j].0  u[j].1  u[4+j].0  u[4+j].1 ]
    ///   v1 = shuf(u[j],   u[4+j],  0xEE) = [ u[j].2  u[j].3  u[4+j].2  u[4+j].3 ]
    ///   v2 = shuf(u[8+j], u[12+j], 0x44),   v3 = shuf(u[8+j], u[12+j], 0xEE)
    ///   m[j]    = shuf(v0, v2, 0x88)   (parts 0 and 2 of each: word j)
    ///   m[4+j]  = shuf(v0, v2, 0xDD)   (parts 1 and 3: word 4+j)
    ///   m[8+j]  = shuf(v1, v3, 0x88),   m[12+j] = shuf(v1, v3, 0xDD)
    /// </code>
    /// </summary>
    public static void LoadTransposed(byte* input, int stride, Vector512<uint>* m)
    {
        var u = stackalloc Vector512<uint>[16];
        for (int k = 0; k < 4; k++)
        {
            var r0 = Vector512.Load((uint*)(input + (4 * k) * stride));
            var r1 = Vector512.Load((uint*)(input + (4 * k + 1) * stride));
            var r2 = Vector512.Load((uint*)(input + (4 * k + 2) * stride));
            var r3 = Vector512.Load((uint*)(input + (4 * k + 3) * stride));
            var t0 = Avx512F.UnpackLow(r0, r1).AsUInt64();
            var t1 = Avx512F.UnpackHigh(r0, r1).AsUInt64();
            var t2 = Avx512F.UnpackLow(r2, r3).AsUInt64();
            var t3 = Avx512F.UnpackHigh(r2, r3).AsUInt64();
            u[4 * k] = Avx512F.UnpackLow(t0, t2).AsUInt32();
            u[4 * k + 1] = Avx512F.UnpackHigh(t0, t2).AsUInt32();
            u[4 * k + 2] = Avx512F.UnpackLow(t1, t3).AsUInt32();
            u[4 * k + 3] = Avx512F.UnpackHigh(t1, t3).AsUInt32();
        }

        for (int j = 0; j < 4; j++)
        {
            var v0 = Avx512F.Shuffle4x128(u[j], u[4 + j], 0x44);
            var v1 = Avx512F.Shuffle4x128(u[j], u[4 + j], 0xEE);
            var v2 = Avx512F.Shuffle4x128(u[8 + j], u[12 + j], 0x44);
            var v3 = Avx512F.Shuffle4x128(u[8 + j], u[12 + j], 0xEE);
            m[j] = Avx512F.Shuffle4x128(v0, v2, 0x88);
            m[4 + j] = Avx512F.Shuffle4x128(v0, v2, 0xDD);
            m[8 + j] = Avx512F.Shuffle4x128(v1, v3, 0x88);
            m[12 + j] = Avx512F.Shuffle4x128(v1, v3, 0xDD);
        }
    }
}

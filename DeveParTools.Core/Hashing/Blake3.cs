using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace DeveParTools.Core.Hashing;

/// <summary>
/// BLAKE3 (unkeyed hash mode). Fully managed, with 8-way (Vector256) / 4-way (Vector128) SIMD chunk
/// hashing and multithreaded subtree hashing for large one-shot inputs.
/// </summary>
public static class Blake3
{
    public const int ChunkLen = 1024;
    public const int BlockLen = 64;
    public const int OutLen = 32;

    internal const uint ChunkStart = 1;
    internal const uint ChunkEnd = 2;
    internal const uint Parent = 4;
    internal const uint Root = 8;

    internal static readonly uint[] IV =
    [
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    ];

    // Message word schedule for each of the 7 rounds (permutation applied cumulatively).
    internal static readonly byte[][] Schedule = BuildSchedule();

    private const int ParallelThreshold = 512 * 1024;

    private static byte[][] BuildSchedule()
    {
        byte[] perm = [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];
        var rounds = new byte[7][];
        var cur = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            cur[i] = (byte)i;
        }

        for (int r = 0; r < 7; r++)
        {
            rounds[r] = (byte[])cur.Clone();
            var next = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                next[i] = cur[perm[i]];
            }

            cur = next;
        }

        return rounds;
    }

    /// <summary>Hash <paramref name="input"/> and write <paramref name="output"/>.Length bytes (≤ 32 here, or extended output).</summary>
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length <= ChunkLen)
        {
            var cs = new ChunkState(IV, 0);
            cs.Update(input);
            cs.Output().RootBytes(output);
            return;
        }

        // Hash all chunks to chaining values (SIMD, parallel), then reduce the tree.
        int chunks = (int)(((long)input.Length + ChunkLen - 1) / ChunkLen);
        var cvs = new uint[(long)chunks * 8];
        HashChunksParallel(input, cvs, chunks);
        var root = ReduceRoot(cvs, 0, chunks);
        root.RootBytes(output);
    }

    /// <summary>Convenience: 32-byte digest.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var r = new byte[OutLen];
        Hash(input, r);
        return r;
    }

    // ---------------------------------------------------------------- tree helpers

    private static void HashChunksParallel(ReadOnlySpan<byte> input, uint[] cvs, int chunks)
    {
        // The final chunk may be partial; it is always hashed with the scalar chunk state.
        int fullChunks = chunks - 1;
        if (input.Length >= ParallelThreshold && Environment.ProcessorCount > 1)
        {
            unsafe
            {
                fixed (byte* p = input)
                {
                    byte* basePtr = p;
                    int len = input.Length;
                    const int Batch = 256; // chunks per work item
                    int batches = (fullChunks + Batch - 1) / Batch;
                    nint addr = (nint)basePtr;
                    Parallel.For(0, batches, b =>
                    {
                        int start = b * Batch;
                        int count = Math.Min(Batch, fullChunks - start);
                        var span = new ReadOnlySpan<byte>((byte*)addr + (long)start * ChunkLen, count * ChunkLen);
                        HashFullChunks(span, (ulong)start, cvs.AsSpan(start * 8, count * 8));
                    });
                }
            }
        }
        else
        {
            HashFullChunks(input[..(fullChunks * ChunkLen)], 0, cvs.AsSpan(0, fullChunks * 8));
        }

        var last = new ChunkState(IV, (ulong)fullChunks);
        last.Update(input[(fullChunks * ChunkLen)..]);
        last.Output().ChainingValue(cvs.AsSpan(fullChunks * 8, 8));
    }

    private static Output ReduceRoot(uint[] cvs, int lo, int hi)
    {
        int n = hi - lo;
        int left = LargestPowerOfTwoBelow(n);
        Span<uint> l = stackalloc uint[8];
        Span<uint> r = stackalloc uint[8];
        Reduce(cvs, lo, lo + left, l);
        Reduce(cvs, lo + left, hi, r);
        return ParentOutput(l, r);
    }

    private static void Reduce(uint[] cvs, int lo, int hi, Span<uint> cvOut)
    {
        int n = hi - lo;
        if (n == 1)
        {
            cvs.AsSpan(lo * 8, 8).CopyTo(cvOut);
            return;
        }

        int left = LargestPowerOfTwoBelow(n);
        Span<uint> l = stackalloc uint[8];
        Span<uint> r = stackalloc uint[8];
        Reduce(cvs, lo, lo + left, l);
        Reduce(cvs, lo + left, hi, r);
        ParentOutput(l, r).ChainingValue(cvOut);
    }

    /// <summary>Largest power of two strictly less than n (n ≥ 2).</summary>
    internal static int LargestPowerOfTwoBelow(int n) => 1 << (31 - BitOperations.LeadingZeroCount((uint)(n - 1)));

    internal static Output ParentOutput(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        var block = new uint[16];
        left[..8].CopyTo(block);
        right[..8].CopyTo(block.AsSpan(8));
        return new Output(IV, block, 0, BlockLen, Parent);
    }

    /// <summary>Hash whole 1024-byte chunks (input length multiple of 1024) into chaining values.</summary>
    internal static void HashFullChunks(ReadOnlySpan<byte> input, ulong firstCounter, Span<uint> cvOut)
    {
        int chunks = input.Length / ChunkLen;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= chunks; i += 8)
            {
                HashChunks8(input.Slice(i * ChunkLen, 8 * ChunkLen), firstCounter + (ulong)i, cvOut.Slice(i * 8, 64));
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= chunks; i += 4)
            {
                HashChunks4(input.Slice(i * ChunkLen, 4 * ChunkLen), firstCounter + (ulong)i, cvOut.Slice(i * 8, 32));
            }
        }

        for (; i < chunks; i++)
        {
            var cs = new ChunkState(IV, firstCounter + (ulong)i);
            cs.Update(input.Slice(i * ChunkLen, ChunkLen));
            cs.Output().ChainingValue(cvOut.Slice(i * 8, 8));
        }
    }

    // ---------------------------------------------------------------- scalar compression

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G(ref uint a, ref uint b, ref uint c, ref uint d, uint mx, uint my)
    {
        a = a + b + mx;
        d = BitOperations.RotateRight(d ^ a, 16);
        c += d;
        b = BitOperations.RotateRight(b ^ c, 12);
        a = a + b + my;
        d = BitOperations.RotateRight(d ^ a, 8);
        c += d;
        b = BitOperations.RotateRight(b ^ c, 7);
    }

    /// <summary>Full compression; writes 16 output words.</summary>
    internal static void Compress(ReadOnlySpan<uint> cv, ReadOnlySpan<uint> m, ulong counter, uint blockLen, uint flags, Span<uint> outWords)
    {
        uint s0 = cv[0], s1 = cv[1], s2 = cv[2], s3 = cv[3], s4 = cv[4], s5 = cv[5], s6 = cv[6], s7 = cv[7];
        uint s8 = IV[0], s9 = IV[1], s10 = IV[2], s11 = IV[3];
        uint s12 = (uint)counter, s13 = (uint)(counter >> 32), s14 = blockLen, s15 = flags;
        for (int r = 0; r < 7; r++)
        {
            var sc = Schedule[r];
            G(ref s0, ref s4, ref s8, ref s12, m[sc[0]], m[sc[1]]);
            G(ref s1, ref s5, ref s9, ref s13, m[sc[2]], m[sc[3]]);
            G(ref s2, ref s6, ref s10, ref s14, m[sc[4]], m[sc[5]]);
            G(ref s3, ref s7, ref s11, ref s15, m[sc[6]], m[sc[7]]);
            G(ref s0, ref s5, ref s10, ref s15, m[sc[8]], m[sc[9]]);
            G(ref s1, ref s6, ref s11, ref s12, m[sc[10]], m[sc[11]]);
            G(ref s2, ref s7, ref s8, ref s13, m[sc[12]], m[sc[13]]);
            G(ref s3, ref s4, ref s9, ref s14, m[sc[14]], m[sc[15]]);
        }

        outWords[0] = s0 ^ s8; outWords[1] = s1 ^ s9; outWords[2] = s2 ^ s10; outWords[3] = s3 ^ s11;
        outWords[4] = s4 ^ s12; outWords[5] = s5 ^ s13; outWords[6] = s6 ^ s14; outWords[7] = s7 ^ s15;
        outWords[8] = s8 ^ cv[0]; outWords[9] = s9 ^ cv[1]; outWords[10] = s10 ^ cv[2]; outWords[11] = s11 ^ cv[3];
        outWords[12] = s12 ^ cv[4]; outWords[13] = s13 ^ cv[5]; outWords[14] = s14 ^ cv[6]; outWords[15] = s15 ^ cv[7];
    }

    internal static void LoadBlockWords(ReadOnlySpan<byte> block, Span<uint> words)
    {
        for (int i = 0; i < 16; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
        }
    }

    /// <summary>The not-yet-compressed final node of a subtree or chunk.</summary>
    internal readonly struct Output
    {
        private readonly uint[] _cv;
        private readonly uint[] _block;
        private readonly ulong _counter;
        private readonly uint _blockLen;
        private readonly uint _flags;

        public Output(uint[] cv, uint[] block, ulong counter, uint blockLen, uint flags)
        {
            _cv = cv;
            _block = block;
            _counter = counter;
            _blockLen = blockLen;
            _flags = flags;
        }

        public void ChainingValue(Span<uint> cvOut)
        {
            Span<uint> o = stackalloc uint[16];
            Compress(_cv, _block, _counter, _blockLen, _flags, o);
            o[..8].CopyTo(cvOut);
        }

        public void RootBytes(Span<byte> output)
        {
            Span<uint> o = stackalloc uint[16];
            ulong outputCounter = 0;
            int pos = 0;
            while (pos < output.Length)
            {
                Compress(_cv, _block, outputCounter, _blockLen, _flags | Root, o);
                for (int w = 0; w < 16 && pos < output.Length; w++)
                {
                    uint word = o[w];
                    for (int b = 0; b < 4 && pos < output.Length; b++)
                    {
                        output[pos++] = (byte)(word >> (8 * b));
                    }
                }

                outputCounter++;
            }
        }
    }

    /// <summary>Incremental state of a single 1024-byte chunk.</summary>
    internal sealed class ChunkState
    {
        private readonly uint[] _cv = new uint[8];
        private readonly byte[] _buf = new byte[BlockLen];
        private int _bufLen;
        private int _blocksCompressed;
        public ulong ChunkCounter { get; }

        public ChunkState(ReadOnlySpan<uint> key, ulong chunkCounter)
        {
            key.CopyTo(_cv);
            ChunkCounter = chunkCounter;
        }

        public int Length => _blocksCompressed * BlockLen + _bufLen;

        private uint StartFlag => _blocksCompressed == 0 ? ChunkStart : 0;

        public void Update(ReadOnlySpan<byte> input)
        {
            Span<uint> words = stackalloc uint[16];
            Span<uint> o = stackalloc uint[16];
            while (input.Length > 0)
            {
                if (_bufLen == BlockLen)
                {
                    LoadBlockWords(_buf, words);
                    Compress(_cv, words, ChunkCounter, BlockLen, StartFlag, o);
                    o[..8].CopyTo(_cv);
                    _blocksCompressed++;
                    _bufLen = 0;
                }

                int take = Math.Min(BlockLen - _bufLen, input.Length);
                input[..take].CopyTo(_buf.AsSpan(_bufLen));
                _bufLen += take;
                input = input[take..];
            }
        }

        public Output Output()
        {
            var words = new uint[16];
            Span<byte> padded = stackalloc byte[BlockLen];
            _buf.AsSpan(0, _bufLen).CopyTo(padded);
            LoadBlockWords(padded, words);
            return new Output((uint[])_cv.Clone(), words, ChunkCounter, (uint)_bufLen, StartFlag | ChunkEnd);
        }
    }

    // ---------------------------------------------------------------- SIMD: N chunks in parallel lanes

    private static void HashChunks8(ReadOnlySpan<byte> input, ulong counter, Span<uint> cvOut)
    {
        var h = new Vector256<uint>[8];
        for (int i = 0; i < 8; i++)
        {
            h[i] = Vector256.Create(IV[i]);
        }

        var ctrLo = Vector256.Create((uint)counter, (uint)(counter + 1), (uint)(counter + 2), (uint)(counter + 3),
                                     (uint)(counter + 4), (uint)(counter + 5), (uint)(counter + 6), (uint)(counter + 7));
        var ctrHi = Vector256.Create((uint)(counter >> 32), (uint)((counter + 1) >> 32), (uint)((counter + 2) >> 32), (uint)((counter + 3) >> 32),
                                     (uint)((counter + 4) >> 32), (uint)((counter + 5) >> 32), (uint)((counter + 6) >> 32), (uint)((counter + 7) >> 32));
        var m = new Vector256<uint>[16];
        for (int blk = 0; blk < 16; blk++)
        {
            int off = blk * BlockLen;
            for (int w = 0; w < 16; w++)
            {
                int o = off + w * 4;
                m[w] = Vector256.Create(
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(2 * ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(3 * ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(4 * ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(5 * ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(6 * ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(7 * ChunkLen + o, 4)));
            }

            uint flags = (blk == 0 ? ChunkStart : 0) | (blk == 15 ? ChunkEnd : 0);
            Compress8(h, m, ctrLo, ctrHi, Vector256.Create((uint)BlockLen), Vector256.Create(flags));
        }

        for (int lane = 0; lane < 8; lane++)
        {
            for (int w = 0; w < 8; w++)
            {
                cvOut[lane * 8 + w] = h[w].GetElement(lane);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Rot(Vector256<uint> x, int n) => Vector256.ShiftRightLogical(x, n) | Vector256.ShiftLeft(x, 32 - n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G8(ref Vector256<uint> a, ref Vector256<uint> b, ref Vector256<uint> c, ref Vector256<uint> d, Vector256<uint> mx, Vector256<uint> my)
    {
        a = a + b + mx;
        d = Rot(d ^ a, 16);
        c += d;
        b = Rot(b ^ c, 12);
        a = a + b + my;
        d = Rot(d ^ a, 8);
        c += d;
        b = Rot(b ^ c, 7);
    }

    private static void Compress8(Vector256<uint>[] h, Vector256<uint>[] m, Vector256<uint> ctrLo, Vector256<uint> ctrHi, Vector256<uint> blockLen, Vector256<uint> flags)
    {
        var s0 = h[0]; var s1 = h[1]; var s2 = h[2]; var s3 = h[3];
        var s4 = h[4]; var s5 = h[5]; var s6 = h[6]; var s7 = h[7];
        var s8 = Vector256.Create(IV[0]); var s9 = Vector256.Create(IV[1]); var s10 = Vector256.Create(IV[2]); var s11 = Vector256.Create(IV[3]);
        var s12 = ctrLo; var s13 = ctrHi; var s14 = blockLen; var s15 = flags;
        for (int r = 0; r < 7; r++)
        {
            var sc = Schedule[r];
            G8(ref s0, ref s4, ref s8, ref s12, m[sc[0]], m[sc[1]]);
            G8(ref s1, ref s5, ref s9, ref s13, m[sc[2]], m[sc[3]]);
            G8(ref s2, ref s6, ref s10, ref s14, m[sc[4]], m[sc[5]]);
            G8(ref s3, ref s7, ref s11, ref s15, m[sc[6]], m[sc[7]]);
            G8(ref s0, ref s5, ref s10, ref s15, m[sc[8]], m[sc[9]]);
            G8(ref s1, ref s6, ref s11, ref s12, m[sc[10]], m[sc[11]]);
            G8(ref s2, ref s7, ref s8, ref s13, m[sc[12]], m[sc[13]]);
            G8(ref s3, ref s4, ref s9, ref s14, m[sc[14]], m[sc[15]]);
        }

        h[0] = s0 ^ s8; h[1] = s1 ^ s9; h[2] = s2 ^ s10; h[3] = s3 ^ s11;
        h[4] = s4 ^ s12; h[5] = s5 ^ s13; h[6] = s6 ^ s14; h[7] = s7 ^ s15;
    }

    private static void HashChunks4(ReadOnlySpan<byte> input, ulong counter, Span<uint> cvOut)
    {
        var h = new Vector128<uint>[8];
        for (int i = 0; i < 8; i++)
        {
            h[i] = Vector128.Create(IV[i]);
        }

        var ctrLo = Vector128.Create((uint)counter, (uint)(counter + 1), (uint)(counter + 2), (uint)(counter + 3));
        var ctrHi = Vector128.Create((uint)(counter >> 32), (uint)((counter + 1) >> 32), (uint)((counter + 2) >> 32), (uint)((counter + 3) >> 32));
        var m = new Vector128<uint>[16];
        for (int blk = 0; blk < 16; blk++)
        {
            int off = blk * BlockLen;
            for (int w = 0; w < 16; w++)
            {
                int o = off + w * 4;
                m[w] = Vector128.Create(
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(2 * ChunkLen + o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(3 * ChunkLen + o, 4)));
            }

            uint flags = (blk == 0 ? ChunkStart : 0) | (blk == 15 ? ChunkEnd : 0);
            Compress4(h, m, ctrLo, ctrHi, Vector128.Create((uint)BlockLen), Vector128.Create(flags));
        }

        for (int lane = 0; lane < 4; lane++)
        {
            for (int w = 0; w < 8; w++)
            {
                cvOut[lane * 8 + w] = h[w].GetElement(lane);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Rot(Vector128<uint> x, int n) => Vector128.ShiftRightLogical(x, n) | Vector128.ShiftLeft(x, 32 - n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G4(ref Vector128<uint> a, ref Vector128<uint> b, ref Vector128<uint> c, ref Vector128<uint> d, Vector128<uint> mx, Vector128<uint> my)
    {
        a = a + b + mx;
        d = Rot(d ^ a, 16);
        c += d;
        b = Rot(b ^ c, 12);
        a = a + b + my;
        d = Rot(d ^ a, 8);
        c += d;
        b = Rot(b ^ c, 7);
    }

    private static void Compress4(Vector128<uint>[] h, Vector128<uint>[] m, Vector128<uint> ctrLo, Vector128<uint> ctrHi, Vector128<uint> blockLen, Vector128<uint> flags)
    {
        var s0 = h[0]; var s1 = h[1]; var s2 = h[2]; var s3 = h[3];
        var s4 = h[4]; var s5 = h[5]; var s6 = h[6]; var s7 = h[7];
        var s8 = Vector128.Create(IV[0]); var s9 = Vector128.Create(IV[1]); var s10 = Vector128.Create(IV[2]); var s11 = Vector128.Create(IV[3]);
        var s12 = ctrLo; var s13 = ctrHi; var s14 = blockLen; var s15 = flags;
        for (int r = 0; r < 7; r++)
        {
            var sc = Schedule[r];
            G4(ref s0, ref s4, ref s8, ref s12, m[sc[0]], m[sc[1]]);
            G4(ref s1, ref s5, ref s9, ref s13, m[sc[2]], m[sc[3]]);
            G4(ref s2, ref s6, ref s10, ref s14, m[sc[4]], m[sc[5]]);
            G4(ref s3, ref s7, ref s11, ref s15, m[sc[6]], m[sc[7]]);
            G4(ref s0, ref s5, ref s10, ref s15, m[sc[8]], m[sc[9]]);
            G4(ref s1, ref s6, ref s11, ref s12, m[sc[10]], m[sc[11]]);
            G4(ref s2, ref s7, ref s8, ref s13, m[sc[12]], m[sc[13]]);
            G4(ref s3, ref s4, ref s9, ref s14, m[sc[14]], m[sc[15]]);
        }

        h[0] = s0 ^ s8; h[1] = s1 ^ s9; h[2] = s2 ^ s10; h[3] = s3 ^ s11;
        h[4] = s4 ^ s12; h[5] = s5 ^ s13; h[6] = s6 ^ s14; h[7] = s7 ^ s15;
    }
}

/// <summary>
/// Streaming BLAKE3 hasher. Large updates are hashed 8/4 chunks at a time with SIMD.
/// </summary>
public sealed class Blake3Hasher
{
    private readonly List<uint[]> _cvStack = new();
    private Blake3.ChunkState _chunk = new(Blake3.IV, 0);
    private ulong _chunksDone;
    private readonly uint[] _scratch = new uint[8 * 64];

    public void Reset()
    {
        _cvStack.Clear();
        _chunk = new Blake3.ChunkState(Blake3.IV, 0);
        _chunksDone = 0;
    }

    public void Update(ReadOnlySpan<byte> input)
    {
        while (input.Length > 0)
        {
            // Fast path: chunk state empty and more than one full chunk available → batch-hash full chunks,
            // always leaving at least one byte for the chunk state (the last chunk must not be finalized early).
            if (_chunk.Length == 0 && input.Length > Blake3.ChunkLen)
            {
                int full = (input.Length - 1) / Blake3.ChunkLen;
                full = Math.Min(full, 64);
                Blake3.HashFullChunks(input[..(full * Blake3.ChunkLen)], _chunksDone, _scratch.AsSpan(0, full * 8));
                for (int i = 0; i < full; i++)
                {
                    AddChunkCv(_scratch.AsSpan(i * 8, 8));
                }

                input = input[(full * Blake3.ChunkLen)..];
                _chunk = new Blake3.ChunkState(Blake3.IV, _chunksDone);
                continue;
            }

            if (_chunk.Length == Blake3.ChunkLen)
            {
                var cv = _scratch.AsSpan(0, 8);
                _chunk.Output().ChainingValue(cv);
                AddChunkCv(cv);
                _chunk = new Blake3.ChunkState(Blake3.IV, _chunksDone);
            }

            int take = Math.Min(Blake3.ChunkLen - _chunk.Length, input.Length);
            _chunk.Update(input[..take]);
            input = input[take..];
        }
    }

    private void AddChunkCv(ReadOnlySpan<uint> cv)
    {
        var newCv = cv.ToArray();
        ulong total = _chunksDone + 1;
        while ((total & 1) == 0)
        {
            var left = _cvStack[^1];
            _cvStack.RemoveAt(_cvStack.Count - 1);
            var merged = new uint[8];
            Blake3.ParentOutput(left, newCv).ChainingValue(merged);
            newCv = merged;
            total >>= 1;
        }

        _cvStack.Add(newCv);
        _chunksDone++;
    }

    public void Finalize(Span<byte> output)
    {
        var outputNode = _chunk.Output();
        for (int i = _cvStack.Count - 1; i >= 0; i--)
        {
            var right = new uint[8];
            outputNode.ChainingValue(right);
            outputNode = Blake3.ParentOutput(_cvStack[i], right);
        }

        outputNode.RootBytes(output);
    }

    public byte[] Finalize()
    {
        var r = new byte[Blake3.OutLen];
        Finalize(r);
        return r;
    }
}

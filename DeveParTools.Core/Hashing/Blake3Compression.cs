using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeveParTools.Core.Hashing;

/// <summary>Eight 32-bit words: a chaining value.</summary>
[InlineArray(8)]
internal struct Blake3Words8
{
    private uint _element;
}

/// <summary>Sixteen 32-bit words: one message block, or one full compression output.</summary>
[InlineArray(16)]
internal struct Blake3Words16
{
    private uint _element;
}

/// <summary>One 64-byte block of input.</summary>
[InlineArray(Blake3.BlockLen)]
internal struct Blake3BlockBytes
{
    private byte _element;
}

/// <summary>
/// The BLAKE3 compression function, one block at a time (scalar). The SIMD version in <see cref="Blake3Simd"/> runs the
/// same rounds on 4, 8 or 16 independent blocks at once.
/// </summary>
/// <remarks>
/// <para>The state is a 4×4 matrix of 32-bit words:</para>
/// <code>
///   s0  s1  s2  s3      ← chaining value, words 0..3
///   s4  s5  s6  s7      ← chaining value, words 4..7
///   s8  s9  s10 s11     ← IV words 0..3
///   s12 s13 s14 s15     ← counter low, counter high, block length, flags
/// </code>
/// <para>A round mixes the four columns with G, then the four diagonals with G. Each G uses two message words.
/// There are 7 rounds, and each round reads the message words in a different order (the schedule in the comments
/// below: round r+1 applies the permutation 2,6,3,10,7,0,4,13,1,11,12,5,9,14,15,8 to round r's order). The rounds are
/// written out with literal indices, so no table is read at run time.</para>
/// <para>The output is <c>s[i] ^ s[i+8]</c> for words 0..7, which is the new chaining value. Words 8..15 are
/// <c>s[i+8] ^ cv[i]</c>, needed only for extended output from the root.</para>
/// </remarks>
internal static class Blake3Compression
{
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

    /// <summary>Compresses message block <paramref name="m"/> (16 words) into <paramref name="output"/> (16 words).</summary>
    [SkipLocalsInit]
    public static void Compress(ReadOnlySpan<uint> cv, ReadOnlySpan<uint> m, ulong counter, uint blockLen, uint flags, Span<uint> output)
    {
        _ = cv[7];
        _ = m[15];
        _ = output[15];
        uint s0 = cv[0], s1 = cv[1], s2 = cv[2], s3 = cv[3], s4 = cv[4], s5 = cv[5], s6 = cv[6], s7 = cv[7];
        uint s8 = Blake3.IV0, s9 = Blake3.IV1, s10 = Blake3.IV2, s11 = Blake3.IV3;
        uint s12 = (uint)counter, s13 = (uint)(counter >> 32), s14 = blockLen, s15 = flags;

        // Round 0: message words 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        G(ref s0, ref s4, ref s8, ref s12, m[0], m[1]);
        G(ref s1, ref s5, ref s9, ref s13, m[2], m[3]);
        G(ref s2, ref s6, ref s10, ref s14, m[4], m[5]);
        G(ref s3, ref s7, ref s11, ref s15, m[6], m[7]);
        G(ref s0, ref s5, ref s10, ref s15, m[8], m[9]);
        G(ref s1, ref s6, ref s11, ref s12, m[10], m[11]);
        G(ref s2, ref s7, ref s8, ref s13, m[12], m[13]);
        G(ref s3, ref s4, ref s9, ref s14, m[14], m[15]);
        // Round 1: message words 2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8
        G(ref s0, ref s4, ref s8, ref s12, m[2], m[6]);
        G(ref s1, ref s5, ref s9, ref s13, m[3], m[10]);
        G(ref s2, ref s6, ref s10, ref s14, m[7], m[0]);
        G(ref s3, ref s7, ref s11, ref s15, m[4], m[13]);
        G(ref s0, ref s5, ref s10, ref s15, m[1], m[11]);
        G(ref s1, ref s6, ref s11, ref s12, m[12], m[5]);
        G(ref s2, ref s7, ref s8, ref s13, m[9], m[14]);
        G(ref s3, ref s4, ref s9, ref s14, m[15], m[8]);
        // Round 2: message words 3, 4, 10, 12, 13, 2, 7, 14, 6, 5, 9, 0, 11, 15, 8, 1
        G(ref s0, ref s4, ref s8, ref s12, m[3], m[4]);
        G(ref s1, ref s5, ref s9, ref s13, m[10], m[12]);
        G(ref s2, ref s6, ref s10, ref s14, m[13], m[2]);
        G(ref s3, ref s7, ref s11, ref s15, m[7], m[14]);
        G(ref s0, ref s5, ref s10, ref s15, m[6], m[5]);
        G(ref s1, ref s6, ref s11, ref s12, m[9], m[0]);
        G(ref s2, ref s7, ref s8, ref s13, m[11], m[15]);
        G(ref s3, ref s4, ref s9, ref s14, m[8], m[1]);
        // Round 3: message words 10, 7, 12, 9, 14, 3, 13, 15, 4, 0, 11, 2, 5, 8, 1, 6
        G(ref s0, ref s4, ref s8, ref s12, m[10], m[7]);
        G(ref s1, ref s5, ref s9, ref s13, m[12], m[9]);
        G(ref s2, ref s6, ref s10, ref s14, m[14], m[3]);
        G(ref s3, ref s7, ref s11, ref s15, m[13], m[15]);
        G(ref s0, ref s5, ref s10, ref s15, m[4], m[0]);
        G(ref s1, ref s6, ref s11, ref s12, m[11], m[2]);
        G(ref s2, ref s7, ref s8, ref s13, m[5], m[8]);
        G(ref s3, ref s4, ref s9, ref s14, m[1], m[6]);
        // Round 4: message words 12, 13, 9, 11, 15, 10, 14, 8, 7, 2, 5, 3, 0, 1, 6, 4
        G(ref s0, ref s4, ref s8, ref s12, m[12], m[13]);
        G(ref s1, ref s5, ref s9, ref s13, m[9], m[11]);
        G(ref s2, ref s6, ref s10, ref s14, m[15], m[10]);
        G(ref s3, ref s7, ref s11, ref s15, m[14], m[8]);
        G(ref s0, ref s5, ref s10, ref s15, m[7], m[2]);
        G(ref s1, ref s6, ref s11, ref s12, m[5], m[3]);
        G(ref s2, ref s7, ref s8, ref s13, m[0], m[1]);
        G(ref s3, ref s4, ref s9, ref s14, m[6], m[4]);
        // Round 5: message words 9, 14, 11, 5, 8, 12, 15, 1, 13, 3, 0, 10, 2, 6, 4, 7
        G(ref s0, ref s4, ref s8, ref s12, m[9], m[14]);
        G(ref s1, ref s5, ref s9, ref s13, m[11], m[5]);
        G(ref s2, ref s6, ref s10, ref s14, m[8], m[12]);
        G(ref s3, ref s7, ref s11, ref s15, m[15], m[1]);
        G(ref s0, ref s5, ref s10, ref s15, m[13], m[3]);
        G(ref s1, ref s6, ref s11, ref s12, m[0], m[10]);
        G(ref s2, ref s7, ref s8, ref s13, m[2], m[6]);
        G(ref s3, ref s4, ref s9, ref s14, m[4], m[7]);
        // Round 6: message words 11, 15, 5, 0, 1, 9, 8, 6, 14, 10, 2, 12, 3, 4, 7, 13
        G(ref s0, ref s4, ref s8, ref s12, m[11], m[15]);
        G(ref s1, ref s5, ref s9, ref s13, m[5], m[0]);
        G(ref s2, ref s6, ref s10, ref s14, m[1], m[9]);
        G(ref s3, ref s7, ref s11, ref s15, m[8], m[6]);
        G(ref s0, ref s5, ref s10, ref s15, m[14], m[10]);
        G(ref s1, ref s6, ref s11, ref s12, m[2], m[12]);
        G(ref s2, ref s7, ref s8, ref s13, m[3], m[4]);
        G(ref s3, ref s4, ref s9, ref s14, m[7], m[13]);

        output[8] = s8 ^ cv[0];
        output[9] = s9 ^ cv[1];
        output[10] = s10 ^ cv[2];
        output[11] = s11 ^ cv[3];
        output[12] = s12 ^ cv[4];
        output[13] = s13 ^ cv[5];
        output[14] = s14 ^ cv[6];
        output[15] = s15 ^ cv[7];
        output[0] = s0 ^ s8;
        output[1] = s1 ^ s9;
        output[2] = s2 ^ s10;
        output[3] = s3 ^ s11;
        output[4] = s4 ^ s12;
        output[5] = s5 ^ s13;
        output[6] = s6 ^ s14;
        output[7] = s7 ^ s15;
    }

    /// <summary>Reads a 64-byte block as 16 little-endian words.</summary>
    public static void LoadWords(ReadOnlySpan<byte> block, Span<uint> words)
    {
        if (!BitConverter.IsLittleEndian) { LoadWordsBigEndianHost(block, words); return; }

        MemoryMarshal.Cast<byte, uint>(block[..Blake3.BlockLen]).CopyTo(words);
    }

    [ExcludeFromCodeCoverage(Justification = "Only runs on big-endian hosts; every supported test and CI machine (x64, arm64) is little-endian.")]
    private static void LoadWordsBigEndianHost(ReadOnlySpan<byte> block, Span<uint> words)
    {
        for (int i = 0; i < 16; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
        }
    }

    /// <summary>Writes the initialisation vector into <paramref name="cv"/>.</summary>
    public static void LoadIV(Span<uint> cv)
    {
        cv[0] = Blake3.IV0;
        cv[1] = Blake3.IV1;
        cv[2] = Blake3.IV2;
        cv[3] = Blake3.IV3;
        cv[4] = Blake3.IV4;
        cv[5] = Blake3.IV5;
        cv[6] = Blake3.IV6;
        cv[7] = Blake3.IV7;
    }
}

/// <summary>
/// A node that has not been compressed yet: either the last block of a chunk or a parent block. It is kept open
/// because it can be compressed in two ways: as an inner node (gives a chaining value) or as the root
/// (with the ROOT flag; gives the digest).
/// </summary>
internal struct Blake3Output
{
    public Blake3Words8 Cv;
    public Blake3Words16 Block;
    public ulong Counter;
    public uint BlockLen;
    public uint Flags;

    /// <summary>A parent node: one block holding the left CV followed by the right CV.</summary>
    public static Blake3Output Parent(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        var output = new Blake3Output { BlockLen = Blake3.BlockLen, Flags = Blake3.Parent };
        Blake3Compression.LoadIV(output.Cv);
        left[..8].CopyTo(output.Block);
        right[..8].CopyTo(((Span<uint>)output.Block)[8..]);
        return output;
    }

    /// <summary>Compresses as an inner node: writes the 8-word chaining value.</summary>
    [SkipLocalsInit]
    public readonly void ChainingValue(Span<uint> cv)
    {
        Span<uint> o = stackalloc uint[16];
        Blake3Compression.Compress(Cv, Block, Counter, BlockLen, Flags, o);
        o[..8].CopyTo(cv);
    }

    /// <summary>
    /// Compresses as the root and fills <paramref name="output"/>. Extended output: the root is compressed again
    /// with output-block counter 0, 1, 2, ..., and each run gives 64 more bytes.
    /// </summary>
    [SkipLocalsInit]
    public readonly void RootBytes(Span<byte> output)
    {
        Span<uint> o = stackalloc uint[16];
        Span<byte> bytes = stackalloc byte[Blake3.BlockLen];
        ulong outputBlock = 0;
        while (!output.IsEmpty)
        {
            Blake3Compression.Compress(Cv, Block, outputBlock++, BlockLen, Flags | Blake3.Root, o);
            for (int w = 0; w < 16; w++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(w * 4, 4), o[w]);
            }

            int take = Math.Min(Blake3.BlockLen, output.Length);
            bytes[..take].CopyTo(output);
            output = output[take..];
        }
    }
}

/// <summary>
/// One chunk that is being filled. The last block of a chunk is never compressed here, because it is the only block
/// that needs the CHUNK_END flag (and maybe ROOT). <see cref="Output"/> returns it as an open node.
/// </summary>
internal struct Blake3ChunkState
{
    private Blake3Words8 _cv;
    private Blake3BlockBytes _buffer;
    private int _bufferLength;
    private int _blocksCompressed;

    public Blake3ChunkState(ulong counter)
    {
        Counter = counter;
        Blake3Compression.LoadIV(_cv);
    }

    /// <summary>Index of this chunk in the input (chunk 0 starts at byte 0).</summary>
    public ulong Counter { get; }

    /// <summary>Bytes absorbed so far (0..1024).</summary>
    public readonly int Length => _blocksCompressed * Blake3.BlockLen + _bufferLength;

    private readonly uint StartFlag => _blocksCompressed == 0 ? Blake3.ChunkStart : 0;

    /// <summary>Absorbs input. The caller never passes more than fits in the chunk.</summary>
    [SkipLocalsInit]
    public void Update(ReadOnlySpan<byte> input)
    {
        Span<uint> words = stackalloc uint[16];
        Span<uint> o = stackalloc uint[16];
        while (!input.IsEmpty)
        {
            // A full buffer is compressed only once more input arrives: the last block must stay open.
            if (_bufferLength == Blake3.BlockLen)
            {
                Blake3Compression.LoadWords(_buffer, words);
                Blake3Compression.Compress(_cv, words, Counter, Blake3.BlockLen, StartFlag, o);
                o[..8].CopyTo(_cv);
                _blocksCompressed++;
                _bufferLength = 0;
            }

            int take = Math.Min(Blake3.BlockLen - _bufferLength, input.Length);
            input[..take].CopyTo(((Span<byte>)_buffer)[_bufferLength..]);
            _bufferLength += take;
            input = input[take..];
        }
    }

    /// <summary>The chunk's last block as an open node (zero-padded; the real length goes in the block-length word).</summary>
    public readonly Blake3Output Output()
    {
        var output = new Blake3Output { Cv = _cv, Counter = Counter, BlockLen = (uint)_bufferLength, Flags = StartFlag | Blake3.ChunkEnd };
        Span<byte> padded = stackalloc byte[Blake3.BlockLen];
        padded.Clear();
        ((ReadOnlySpan<byte>)_buffer)[.._bufferLength].CopyTo(padded);
        Blake3Compression.LoadWords(padded, output.Block);
        return output;
    }
}

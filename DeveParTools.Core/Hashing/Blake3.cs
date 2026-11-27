namespace DeveParTools.Core.Hashing;

/// <summary>
/// BLAKE3 in its plain (unkeyed) hash mode. Fully managed, using SIMD on 4, 8 or 16 chunks at once and all cores for large
/// inputs. Used by PAR3 for packet checksums, block hashes and file hashes.
/// </summary>
/// <remarks>
/// <para><b>How BLAKE3 hashes a message.</b> The input is cut into 1024-byte <i>chunks</i>, and every chunk into sixteen
/// 64-byte <i>blocks</i>. A chunk is hashed by running the compression function once per block. Each run takes the
/// previous 8-word (32-byte) state and returns the next one. The final state is the chunk's <i>chaining value</i>
/// (CV). The CVs are then combined two at a time in a binary tree: a <i>parent</i> node compresses one 64-byte block
/// made of its left child's CV followed by its right child's CV. The topmost node is compressed with the ROOT flag and
/// gives the digest. Any length can be read out of the root ("extendable output"). PAR3 uses the first 16 or 32 bytes.</para>
///
/// <para><b>Shape of the tree.</b> The tree is "left-full". For <c>n</c> chunks, the left subtree takes the largest
/// power of two that is less than <c>n</c>, and the right subtree takes the rest. With 5 chunks C0..C4:</para>
/// <code>
///                         ROOT = parent(P0123, C4)
///                        /                         \
///          P0123 = parent(P01, P23)                 C4    ← an odd node is carried up unchanged
///           /                 \
///   P01 = parent(C0, C1)   P23 = parent(C2, C3)
/// </code>
/// <para>You get exactly this shape by merging neighbours in pairs, level by level, and carrying an odd last node up
/// to the next level:</para>
/// <code>
///   level 0:  C0  C1  C2  C3  C4
///   level 1:  P01     P23     C4         (C4 has no partner: carried)
///   level 2:  P0123           C4
///   level 3:  ROOT
/// </code>
/// <para><see cref="Blake3Tree.ReduceTo"/> uses this level-by-level form, because then all parents on one level are
/// independent and can be compressed with SIMD (8 parents at once with AVX2).</para>
///
/// <para><b>Flags.</b> Every compression gets flags: CHUNK_START on a chunk's first block, CHUNK_END on its last block,
/// PARENT for parent nodes and ROOT for the last compression. Because of ROOT, no node can be finished until we know
/// it is not the root. The streaming <see cref="Blake3Hasher"/> therefore keeps the last chunk, or the last two
/// subtree CVs, pending until more input arrives ("lazy merging").</para>
///
/// <para><b>Where the speed comes from.</b> The 16 blocks of one chunk depend on each other, but different chunks do
/// not. <see cref="Blake3Simd"/> hashes 4, 8 or 16 chunks at the same time, one chunk per vector lane. Large inputs are
/// also split into aligned subtrees of 256 chunks that are hashed on all cores.</para>
/// </remarks>
public static class Blake3
{
    /// <summary>Bytes per chunk (a leaf of the hash tree).</summary>
    public const int ChunkLen = 1024;

    /// <summary>Bytes per compression-function block.</summary>
    public const int BlockLen = 64;

    /// <summary>Default digest length in bytes.</summary>
    public const int OutLen = 32;

    internal const int BlocksPerChunk = ChunkLen / BlockLen;

    internal const uint ChunkStart = 1;
    internal const uint ChunkEnd = 2;
    internal const uint Parent = 4;
    internal const uint Root = 8;

    // The initialisation vector: the same eight words as SHA-256's initial state.
    internal const uint IV0 = 0x6A09E667;
    internal const uint IV1 = 0xBB67AE85;
    internal const uint IV2 = 0x3C6EF372;
    internal const uint IV3 = 0xA54FF53A;
    internal const uint IV4 = 0x510E527F;
    internal const uint IV5 = 0x9B05688C;
    internal const uint IV6 = 0x1F83D9AB;
    internal const uint IV7 = 0x5BE0CD19;

    /// <summary>
    /// Hashes <paramref name="input"/> and fills <paramref name="output"/>. It can be any length: 32 bytes is the
    /// normal digest, shorter output is a prefix of it, and longer output is BLAKE3's extended output.
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length <= ChunkLen)
        {
            // A single chunk is its own root: no tree and no allocation.
            var chunk = new Blake3ChunkState(0);
            chunk.Update(input);
            chunk.Output().RootBytes(output);
            return;
        }

        var state = new Blake3State();
        state.Update(input);
        state.Finalize(output);
    }

    /// <summary>Hashes <paramref name="input"/> and returns the 32-byte digest.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var digest = new byte[OutLen];
        Hash(input, digest);
        return digest;
    }
}

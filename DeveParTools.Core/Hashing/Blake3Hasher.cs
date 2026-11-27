using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DeveParTools.Core.Hashing;

/// <summary>
/// Streaming BLAKE3: feed the input with <see cref="Update"/> in pieces of any size, then call
/// <see cref="Finalize(Span{byte})"/>. Gives the same result as <see cref="Blake3.Hash(ReadOnlySpan{byte})"/> on the
/// whole input. Large updates use SIMD and all cores.
/// </summary>
public sealed class Blake3Hasher
{
    private Blake3State _state = new();

    /// <summary>Starts over with empty input.</summary>
    public void Reset() => _state = new Blake3State();

    /// <summary>Adds more input.</summary>
    public void Update(ReadOnlySpan<byte> input) => _state.Update(input);

    /// <summary>Writes the hash of everything added so far (any output length; 32 bytes is the normal digest).</summary>
    public void Finalize(Span<byte> output) => _state.Finalize(output);

    /// <summary>Returns the 32-byte hash of everything added so far.</summary>
    public byte[] Finalize()
    {
        var digest = new byte[Blake3.OutLen];
        Finalize(digest);
        return digest;
    }
}

/// <summary>Room for the chaining-value stack: 2^54 chunks is 2^64 bytes, plus one entry for lazy merging.</summary>
[InlineArray(Blake3State.MaxStackDepth * 8)]
internal struct Blake3CvStack
{
    private uint _element;
}

/// <summary>
/// The incremental tree: the chunk being filled plus a stack of chaining values for finished subtrees.
/// </summary>
/// <remarks>
/// <para><b>The stack.</b> After n finished chunks, the finished part of the tree is a list of complete subtrees whose
/// sizes are the binary digits of n. For n = 11 = 0b1011 that is a subtree of 8 chunks, one of 2 and one of 1:</para>
/// <code>
///   stack: [ CV(chunks 0-7) , CV(chunks 8-9) , CV(chunk 10) ]
/// </code>
/// <para>When chunk 11 finishes, the counter goes to 12 = 0b1100, which has 2 one-bits. So the stack is merged
/// until it has 2 entries: CV(10) and CV(11) become CV(10-11), which merges with CV(8-9) into CV(8-11). The stack is now
/// [CV(0-7), CV(8-11)].</para>
/// <para><b>Lazy merging.</b> A merge is done only when more input is known to follow. Otherwise the two CVs could be
/// the children of the root, and the root has to be compressed with the ROOT flag. <see cref="PushCv"/> therefore merges
/// for the CVs that were already on the stack, never for the one it pushes.</para>
/// <para><b>Big updates.</b> When the chunk buffer is empty and more than one chunk of input is left, the largest
/// whole subtree is taken in one go: a power-of-two number of chunks that also starts at a multiple of its own size
/// (subtrees in the BLAKE3 tree are aligned). <see cref="Blake3Tree.CompressSubtreeToTwo"/> hashes it with SIMD and
/// threads down to its two child CVs, which are pushed like two chunks.</para>
/// </remarks>
internal struct Blake3State
{
    internal const int MaxStackDepth = 55;

    /// <summary>Largest subtree taken in one step; bounds the scratch memory (64 MiB of input → 2 MiB of CVs).</summary>
    internal const long MaxSubtreeLen = 64L << 20;

    private Blake3ChunkState _chunk;
    private Blake3CvStack _stack;
    private int _stackLength;

    public Blake3State()
    {
        _chunk = new Blake3ChunkState(0);
    }

    [SkipLocalsInit]
    public void Update(ReadOnlySpan<byte> input)
    {
        Span<uint> cvs = stackalloc uint[16];
        if (_chunk.Length > 0)
        {
            int take = Math.Min(Blake3.ChunkLen - _chunk.Length, input.Length);
            _chunk.Update(input[..take]);
            input = input[take..];
            if (input.IsEmpty)
            {
                return;
            }

            // The chunk is full and more input follows, so it is not the root: finish it.
            _chunk.Output().ChainingValue(cvs);
            PushCv(cvs, _chunk.Counter);
            _chunk = new Blake3ChunkState(_chunk.Counter + 1);
        }

        while (input.Length > Blake3.ChunkLen)
        {
            ulong counter = _chunk.Counter;
            long subtreeLen = Math.Min(MaxSubtreeLen, 1L << (63 - BitOperations.LeadingZeroCount((ulong)input.Length)));
            ulong subtreeChunks = (ulong)subtreeLen / Blake3.ChunkLen;
            while ((counter & (subtreeChunks - 1)) != 0)
            {
                subtreeChunks >>= 1;
            }

            int bytes = (int)subtreeChunks * Blake3.ChunkLen;
            if (subtreeChunks == 1)
            {
                Blake3Tree.HashChunks(input[..bytes], counter, cvs[..8]);
                PushCv(cvs[..8], counter);
            }
            else
            {
                Blake3Tree.CompressSubtreeToTwo(input[..bytes], counter, cvs);
                PushCv(cvs[..8], counter);
                PushCv(cvs[8..], counter + subtreeChunks / 2);
            }

            _chunk = new Blake3ChunkState(counter + subtreeChunks);
            input = input[bytes..];
        }

        if (!input.IsEmpty)
        {
            _chunk.Update(input);

            // Input continues past the stack, so everything on it can be merged now.
            MergeStack(_chunk.Counter);
        }
    }

    /// <summary>Merges finished subtrees until the stack has as many entries as <paramref name="totalChunks"/> has one-bits.</summary>
    private void MergeStack(ulong totalChunks)
    {
        Span<uint> stack = _stack;
        int target = BitOperations.PopCount(totalChunks);
        while (_stackLength > target)
        {
            var left = stack.Slice((_stackLength - 2) * 8, 8);
            var right = stack.Slice((_stackLength - 1) * 8, 8);
            Blake3Output.Parent(left, right).ChainingValue(left);
            _stackLength--;
        }
    }

    /// <summary>Pushes the CV of a subtree that starts at chunk <paramref name="chunkCounter"/>.</summary>
    private void PushCv(ReadOnlySpan<uint> cv, ulong chunkCounter)
    {
        MergeStack(chunkCounter);
        cv[..8].CopyTo(((Span<uint>)_stack).Slice(_stackLength * 8, 8));
        _stackLength++;
    }

    /// <summary>
    /// Builds the root from right to left: the open chunk (or the top two CVs when the input ended on a subtree
    /// boundary), then a parent with each remaining stack entry. Only the last of these gets the ROOT flag.
    /// </summary>
    [SkipLocalsInit]
    public readonly void Finalize(Span<byte> output)
    {
        if (_stackLength == 0)
        {
            _chunk.Output().RootBytes(output);
            return;
        }

        ReadOnlySpan<uint> stack = _stack;
        Span<uint> cv = stackalloc uint[8];
        int remaining = _stackLength;
        Blake3Output node;
        if (_chunk.Length > 0)
        {
            node = _chunk.Output();
        }
        else
        {
            node = Blake3Output.Parent(stack.Slice((remaining - 2) * 8, 8), stack.Slice((remaining - 1) * 8, 8));
            remaining -= 2;
        }

        while (remaining > 0)
        {
            node.ChainingValue(cv);
            node = Blake3Output.Parent(stack.Slice((remaining - 1) * 8, 8), cv);
            remaining--;
        }

        node.RootBytes(output);
    }
}

/// <summary>Hashing whole chunks and whole subtrees into chaining values (SIMD and multithreaded).</summary>
internal static class Blake3Tree
{
    /// <summary>Subtrees at least this large are hashed on all cores.</summary>
    internal const int ParallelThreshold = 1 << 20;

    /// <summary>Chunks per parallel work item. A power of two, so each work item is a complete, aligned subtree.</summary>
    internal const int BatchChunks = 256;

    /// <summary>Hashes whole chunks (input length a multiple of 1024) into one CV each.</summary>
    public static unsafe void HashChunks(ReadOnlySpan<byte> input, ulong counter, Span<uint> cvOut)
    {
        int chunks = input.Length / Blake3.ChunkLen;
        fixed (byte* p = input)
        fixed (uint* o = cvOut)
        {
            Blake3Simd.HashMany(p, chunks, Blake3.ChunkLen, Blake3.BlocksPerChunk, counter, true, 0, Blake3.ChunkStart, Blake3.ChunkEnd, o);
        }
    }

    /// <summary>
    /// Compresses <paramref name="parents"/> parent nodes. Parent p's block is children CVs 2p and 2p+1, which sit next
    /// to each other in memory (64 bytes). Output may overlap the input (in-place reduction): parent p writes slot p
    /// only after reading slots 2p and 2p+1, and the lanes run in increasing order.
    /// </summary>
    public static unsafe void HashParents(ReadOnlySpan<uint> children, int parents, Span<uint> cvOut)
    {
        fixed (uint* c = children)
        fixed (uint* o = cvOut)
        {
            Blake3Simd.HashMany((byte*)c, parents, Blake3.BlockLen, 1, 0, false, Blake3.Parent, 0, 0, o);
        }
    }

    /// <summary>
    /// Merges <paramref name="count"/> CVs in place, level by level, until <paramref name="target"/> remain. This builds
    /// the same tree as BLAKE3's "largest power of two on the left" rule (see the remarks on <see cref="Blake3"/>).
    /// </summary>
    public static void ReduceTo(Span<uint> cvs, int count, int target)
    {
        while (count > target)
        {
            int parents = count / 2;
            HashParents(cvs, parents, cvs);
            if ((count & 1) != 0)
            {
                // The odd last node has no partner on this level and moves up as it is.
                cvs.Slice((count - 1) * 8, 8).CopyTo(cvs.Slice(parents * 8, 8));
            }

            count = parents + (count & 1);
        }
    }

    /// <summary>
    /// Hashes a complete subtree of 2^k chunks (k ≥ 1) starting at chunk <paramref name="counter"/> and writes its two
    /// children's CVs (16 words). The subtree's own node is not compressed: it could be the root.
    /// </summary>
    public static unsafe void CompressSubtreeToTwo(ReadOnlySpan<byte> input, ulong counter, Span<uint> twoCvs)
    {
        int chunks = input.Length / Blake3.ChunkLen;
        var cvs = ArrayPool<uint>.Shared.Rent(chunks * 8);
        try
        {
            if (input.Length >= ParallelThreshold && Environment.ProcessorCount > 1)
            {
                // Each batch is an aligned power-of-two run of chunks, hence a complete subtree on its own: reduce it
                // to one CV on its thread, then reduce the batch CVs.
                int batches = chunks / BatchChunks;
                fixed (byte* p = input)
                {
                    nint address = (nint)p;
                    Parallel.For(0, batches, b =>
                    {
                        var slice = new ReadOnlySpan<byte>((byte*)address + (long)b * BatchChunks * Blake3.ChunkLen, BatchChunks * Blake3.ChunkLen);
                        var own = cvs.AsSpan(b * BatchChunks * 8, BatchChunks * 8);
                        HashChunks(slice, counter + (ulong)(b * BatchChunks), own);
                        ReduceTo(own, BatchChunks, 1);
                    });
                }

                // Batch b's CV is at slot b × BatchChunks; gather them at slots 0..batches-1 (in order, so no source
                // is overwritten before it is read).
                for (int b = 1; b < batches; b++)
                {
                    cvs.AsSpan(b * BatchChunks * 8, 8).CopyTo(cvs.AsSpan(b * 8, 8));
                }

                ReduceTo(cvs, batches, 2);
            }
            else
            {
                HashChunks(input, counter, cvs.AsSpan(0, chunks * 8));
                ReduceTo(cvs, chunks, 2);
            }

            cvs.AsSpan(0, 16).CopyTo(twoCvs);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(cvs);
        }
    }
}

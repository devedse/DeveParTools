using System.Runtime.InteropServices;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Codes;

/// <summary>Supplies byte ranges of the input regions (blocks) of a linear combination.</summary>
public interface IRegionSource
{
    /// <summary>
    /// Fill <paramref name="destination"/> with bytes [offset, offset + destination.Length) of input
    /// <paramref name="index"/>. Bytes beyond the end of the input must be zero.
    /// </summary>
    void Read(int index, long offset, Span<byte> destination);
}

/// <summary>Receives byte ranges of the computed output regions.</summary>
public interface IRegionSink
{
    void Write(int index, long offset, ReadOnlySpan<byte> source);
}

/// <summary>
/// A sink whose <see cref="IRegionSink.Write"/> may be called for different outputs at the same time: the combiner
/// then writes the outputs of a pass in parallel (a sink that hashes what it writes, as the recovery files do, hashes
/// them in parallel too) instead of one after the other.
/// </summary>
public interface IConcurrentRegionSink : IRegionSink
{
}

/// <summary>Supplies coefficient (output, input) without materialising huge matrices.</summary>
public interface ICoefficientProvider<T>
{
    /// <summary>Fill <paramref name="destination"/>[i] = coefficient(output, firstInput + i).</summary>
    void Fill(int output, int firstInput, Span<T> destination);
}

public sealed class MatrixCoefficients<T>(GfMatrix<T> matrix) : ICoefficientProvider<T> where T : IEquatable<T>
{
    public void Fill(int output, int firstInput, Span<T> destination)
        => matrix.Row(output).Slice(firstInput, destination.Length).CopyTo(destination);
}

public sealed class LinearCombineOptions
{
    /// <summary>Upper bound for output + input buffers in bytes.</summary>
    public long MemoryBudget { get; init; } = 1L << 30;

    public int Threads { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Inputs combined per batch: each output goes through memory once per batch, so more means less memory traffic
    /// (see <see cref="LinearCombiner"/>), at the cost of two input buffers of this many slices.
    /// </summary>
    public int SourcesPerBatch { get; init; } = 64;

    public IProgress<double>? Progress { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Computes <c>output[j] = Σ_i coef(j, i) · input[i]</c> over byte regions, multithreaded, with SIMD kernels,
/// double-buffered input reads and a memory budget (large outputs are processed in slices).
/// </summary>
/// <remarks>
/// <para><b>Why the loops look like this.</b> PAR2 with 5,316 input and 5,316 recovery blocks of 2.69 MiB is 80 TB of
/// multiply-adds: 4 min 43 s at 17.6 GB/s per thread on 16 cores, but only while the data the kernel works on is in
/// the CPU caches and the kernel does little besides multiplying. Per pass (a slice of every block that fits the
/// memory budget):</para>
/// <code>
/// for each batch of B inputs (read in the background, converted to the kernel's layout)   B = 64
///     the non-zero coefficients of every output for this batch                           (in parallel)
///     for each tile = chunk c × group of outputs                                         (in parallel)
///         for each output j in the group
///             for each input i of the batch
///                 out[j][c] ^= coef(j, i) · in[i][c]                                    chunk c: 8 KiB
/// convert the outputs back and write them (in parallel when the sink allows it)
/// </code>
/// <list type="bullet">
/// <item>Main memory sees each output once per batch, so once per 64 inputs. (The earlier loop added 16 inputs to
/// each output's whole slice at a time: for those 5,316 blocks some 10 TB went through main memory, and the CPU mostly
/// waited for it.)</item>
/// <item>The input chunks of a tile (B × chunk = 512 KiB) stay in the core's L2 cache while every output of the tile
/// uses them; the output's chunk stays in the L1 cache while the B inputs are added to it.</item>
/// <item>The kernel takes one input at a time, with its lookup tables in registers, built from the coefficient in a
/// few dozen cycles (<see cref="IPreparedKernel{T}"/>). There are no tables in memory, from which a kernel that adds
/// several inputs at once loads 8 vectors per input for every 64 bytes.</item>
/// <item>The data stays in the kernel's own layout while it is worked on (GF(2^16): the low and the high bytes of
/// the symbols apart), so the kernel does not split the bytes of every input again for every output.</item>
/// <item>The inputs of a batch are an odd number of 4 KiB pages apart (<see cref="InputStride"/>): at a power of
/// two, the same chunk of all inputs would compete for the same few sets of the cache.</item>
/// </list>
/// <para>Measured with 4 threads (AVX2): 1,000 × 1,000 blocks of 256 KiB in memory, 97 GB/s with the earlier loop and
/// 159 GB/s now; creating PAR2 with 2,000 + 2,000 blocks of 256 KiB, 13 to 14 s before and 7.8 s now
/// (par2cmdline-turbo: 7.3 s). Fields without a prepared kernel (GF(2^24) and up, whose tables are large) use the
/// simple loop: per output, its tables, its whole slice.</para>
/// </remarks>
public static unsafe class LinearCombiner
{
    /// <summary>
    /// Inputs per batch × chunk length: the input data a tile keeps in the L2 cache. 256 KiB and 1 MiB were measured
    /// too: 8% slower and 2% slower.
    /// </summary>
    internal const int TileInputBytes = 512 * 1024;

    public static void Combine<T>(
        IGaloisField<T> field,
        ICoefficientProvider<T> coefficients,
        int inputCount,
        int outputCount,
        IRegionSource sources,
        IRegionSink sinks,
        long regionLength,
        LinearCombineOptions? options = null)
        where T : IEquatable<T>
    {
        options ??= new LinearCombineOptions();
        if (outputCount == 0 || regionLength == 0)
        {
            return;
        }

        int sym = field.SymbolSize;
        int batch = Math.Max(1, Math.Min(options.SourcesPerBatch, Math.Max(1, inputCount)));
        int sliceLen = PlanSliceLength(sym, inputCount, outputCount, regionLength, options);
        long passes = (regionLength + sliceLen - 1) / sliceLen;
        int batches = Math.Max(1, (inputCount + batch - 1) / batch);
        long totalSteps = passes * batches;
        long stepsDone = 0;

        long align = Alignment(sym);
        var kernel = (field as IPreparedRegionField<T>)?.PreparedKernel();
        int chunkLen = PlanChunkLength(sym, batch, sliceLen);
        var coefficientsOf = kernel != null ? new BatchCoefficients<T>(outputCount, batch) : null;

        byte* outputs = (byte*)NativeMemory.AlignedAlloc((nuint)((long)outputCount * sliceLen), 64);
        int inputStride = InputStride(sliceLen);
        byte* inA = (byte*)NativeMemory.AlignedAlloc((nuint)((long)batch * inputStride), 64);
        byte* inB = (byte*)NativeMemory.AlignedAlloc((nuint)((long)batch * inputStride), 64);

        // The background read of the next input batch writes into inA/inB, so it must have finished before they are
        // freed, also when combining fails or is cancelled while the read is still running.
        Task? reader = null;
        try
        {
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Threads),
                CancellationToken = options.CancellationToken,
            };

            for (long offset = 0; offset < regionLength; offset += sliceLen)
            {
                int len = (int)Math.Min(sliceLen, regionLength - offset);
                // The sources fill the slice rounded up to whole symbols, as always (callers that hash what they
                // read rely on that); the kernels take it rounded up to the alignment, the rest zeros.
                int lenRead = (int)RoundUp(len, sym);
                int lenAligned = (int)RoundUp(len, align);

                if (inputCount == 0)
                {
                    // No inputs: every output is zero, the padding too (Finish mixes it with the bytes before it).
                    // Cleared per output: outputCount · sliceLen can exceed int.
                    for (int j = 0; j < outputCount; j++)
                    {
                        new Span<byte>(outputs + (long)j * sliceLen, lenAligned).Clear();
                    }
                }

                // Prime the first batch.
                int firstCount = Math.Min(batch, inputCount);
                ReadBatch(sources, kernel, 0, firstCount, offset, lenRead, lenAligned, inputStride, inA);

                for (int b = 0; b < batches && inputCount > 0; b++)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    int first = b * batch;
                    int count = Math.Min(batch, inputCount - first);
                    byte* cur = (b & 1) == 0 ? inA : inB;
                    byte* next = (b & 1) == 0 ? inB : inA;

                    // Read the next batch while this one is being combined.
                    reader = null;
                    if (b + 1 < batches)
                    {
                        int nFirst = first + batch;
                        int nCount = Math.Min(batch, inputCount - nFirst);
                        nint nextPtr = (nint)next;
                        long off = offset;
                        reader = Task.Run(() => ReadBatch(sources, kernel, nFirst, nCount, off, lenRead, lenAligned, inputStride, (byte*)nextPtr));
                    }

                    var step = new BatchStep<T>(field, coefficients, first, count, batch, (nint)cur, inputStride, (nint)outputs, sliceLen, lenAligned, b == 0);
                    if (kernel != null)
                    {
                        CombineTiled(step, kernel, coefficientsOf!, outputCount, chunkLen, parallel);
                    }
                    else
                    {
                        CombinePerOutput(step, outputCount, parallel);
                    }

                    reader?.Wait(options.CancellationToken);
                    stepsDone++;
                    options.Progress?.Report((double)stepsDone / totalSteps);
                }

                // Back from the kernel's layout to the normal one (all outputs at once), then out.
                nint outputsPtr = (nint)outputs;
                long sliceOffset = offset;
                bool concurrent = sinks is IConcurrentRegionSink;
                if (kernel != null || concurrent)
                {
                    Parallel.For(0, outputCount, parallel, j =>
                    {
                        byte* output = (byte*)outputsPtr + (long)j * sliceLen;
                        kernel?.Finish(output, lenAligned);
                        if (concurrent)
                        {
                            sinks.Write(j, sliceOffset, new ReadOnlySpan<byte>(output, len));
                        }
                    });
                }

                for (int j = 0; j < outputCount && !concurrent; j++)
                {
                    sinks.Write(j, offset, new ReadOnlySpan<byte>(outputs + (long)j * sliceLen, len));
                }
            }
        }
        finally
        {
            if (reader != null)
            {
                // Waits without throwing: an exception from the read itself was already observed above, or is
                // superseded by the one that is propagating now.
                Task.WaitAny(reader);
            }

            NativeMemory.AlignedFree(outputs);
            NativeMemory.AlignedFree(inA);
            NativeMemory.AlignedFree(inB);
        }
    }

    /// <summary>One batch of inputs in one pass: what both loops need.</summary>
    private readonly record struct BatchStep<T>(
        IGaloisField<T> Field, ICoefficientProvider<T> Coefficients, int First, int Count, int Batch,
        nint Inputs, int InputStride, nint Outputs, int SliceLen, int LenAligned, bool Overwrite)
        where T : IEquatable<T>
    {
        /// <summary>The non-zero coefficients of output <paramref name="j"/> in this batch, and which inputs they belong to.</summary>
        public int NonZero(int j, Span<T> coefs, Span<int> inputs)
        {
            Coefficients.Fill(j, First, coefs[..Count]);
            int used = 0;
            for (int i = 0; i < Count; i++)
            {
                if (!Field.IsZero(coefs[i]))
                {
                    coefs[used] = coefs[i];
                    inputs[used] = i;
                    used++;
                }
            }

            return used;
        }

        public byte* Output(int j) => (byte*)Outputs + (long)j * SliceLen;

        public byte* Input(int i) => (byte*)Inputs + (long)i * InputStride;
    }

    /// <summary>The non-zero coefficients of every output in a batch, and which inputs of the batch they belong to.</summary>
    private sealed class BatchCoefficients<T>(int outputs, int batch)
    {
        public int Batch { get; } = batch;

        public T[] Coefficients { get; } = new T[(long)outputs * batch];

        public int[] Inputs { get; } = new int[(long)outputs * batch];

        public int[] Used { get; } = new int[outputs];
    }

    /// <summary>The cache-friendly loop (see the class remarks): the coefficients of every output, then the tiles.</summary>
    private static void CombineTiled<T>(BatchStep<T> step, IPreparedKernel<T> kernel, BatchCoefficients<T> of, int outputCount, int chunkLen, ParallelOptions parallel)
        where T : IEquatable<T>
    {
        int batch = of.Batch;
        Parallel.For(0, outputCount, parallel, j =>
            of.Used[j] = step.NonZero(j, of.Coefficients.AsSpan(j * batch, batch), of.Inputs.AsSpan(j * batch, batch)));

        int chunks = (step.LenAligned + chunkLen - 1) / chunkLen;
        int groups = Math.Clamp((parallel.MaxDegreeOfParallelism * 4 + chunks - 1) / chunks, 1, outputCount);
        int perGroup = (outputCount + groups - 1) / groups;
        Parallel.For(0, (long)chunks * groups, parallel, () => (nint)NativeMemory.Alloc((nuint)(batch * sizeof(nint))), (tile, _, state) =>
        {
            int c = (int)(tile % chunks);
            int g = (int)(tile / chunks);
            int start = c * chunkLen;
            int length = Math.Min(chunkLen, step.LenAligned - start);
            byte** ptrs = (byte**)state;
            for (int j = g * perGroup; j < Math.Min(outputCount, (g + 1) * perGroup); j++)
            {
                int used = of.Used[j];
                for (int u = 0; u < used; u++)
                {
                    ptrs[u] = step.Input(of.Inputs[(long)j * batch + u]) + start;
                }

                kernel.MulAddMulti(of.Coefficients.AsSpan(j * batch, used), ptrs, step.Output(j) + start, length, step.Overwrite);
            }

            return state;
        }, state => NativeMemory.Free((void*)state));
    }

    /// <summary>The simple loop for fields with large tables: per output, its tables and its whole slice.</summary>
    private static void CombinePerOutput<T>(BatchStep<T> step, int outputCount, ParallelOptions parallel)
        where T : IEquatable<T>
    {
        Parallel.For(0, outputCount, parallel,
            () => (coefs: new T[step.Batch], inputs: new int[step.Batch], ptrs: (nint)NativeMemory.Alloc((nuint)(step.Batch * sizeof(nint)))),
            (j, _, state) =>
            {
                int used = step.NonZero(j, state.coefs, state.inputs);
                byte* dst = step.Output(j);
                if (used == 0)
                {
                    if (step.Overwrite)
                    {
                        new Span<byte>(dst, step.LenAligned).Clear();
                    }

                    return state;
                }

                byte** ptrs = (byte**)state.ptrs;
                for (int u = 0; u < used; u++)
                {
                    ptrs[u] = step.Input(state.inputs[u]);
                }

                using var tables = step.Field.CreateTables(state.coefs.AsSpan(0, used));
                step.Field.MulAddMulti(tables, 0, used, ptrs, dst, step.LenAligned, step.Overwrite);
                return state;
            },
            state => NativeMemory.Free((void*)state.ptrs));
    }

    /// <summary>
    /// The chunk of a tile: <see cref="TileInputBytes"/> ÷ inputs per batch (8 KiB for 64 inputs), at least 1 KiB, a
    /// multiple of the alignment (128 bytes and the symbol size), at most the slice.
    /// </summary>
    internal static int PlanChunkLength(int symbolSize, int batch, int sliceLen)
    {
        long align = Alignment(symbolSize);
        long chunk = Math.Max(1024, TileInputBytes / Math.Max(1, batch));
        chunk = Math.Max(align, chunk / align * align);
        return (int)Math.Min(chunk, RoundUp(sliceLen, align));
    }

    /// <summary>
    /// Length of the region slices processed per pass: all outputs plus two input batches must fit in the memory
    /// budget. A multiple of 128 bytes and of the symbol size. When it is ≥ the region length, everything is done in
    /// one pass and every input byte is read exactly once (in order), which callers use to hash while encoding.
    /// </summary>
    public static int PlanSliceLength(int symbolSize, int inputCount, int outputCount, long regionLength, LinearCombineOptions options)
    {
        int batch = Math.Max(1, Math.Min(options.SourcesPerBatch, Math.Max(1, inputCount)));
        long align = Alignment(symbolSize);
        long perByte = Math.Max(1, outputCount) + 2L * batch;
        long slice = Math.Max(align, options.MemoryBudget / perByte);
        slice = Math.Min(slice, RoundUp(Math.Max(1, regionLength), align));
        slice = Math.Max(align, slice / align * align);
        if (slice > int.MaxValue / 2)
        {
            slice = int.MaxValue / 2 / align * align;
        }

        return (int)slice;
    }

    /// <summary>
    /// The distance between the inputs of a batch in its buffer: the slice length rounded up to an odd number of
    /// 4 KiB pages. With a power-of-two distance (slices of 256 KiB, say) the same chunk of all 64 inputs would sit in
    /// the same few sets of the CPU cache, which holds only 8 to 16 lines per set: the inputs of a tile would push
    /// each other out of the cache. An odd number of pages spreads them over all sets.
    /// </summary>
    internal static int InputStride(int sliceLen)
    {
        long pages = (sliceLen + 4095L) / 4096;
        return (int)((pages | 1) * 4096);
    }

    /// <summary>
    /// Reads <paramref name="len"/> bytes of inputs [first, first + count) of a batch, pads them with zeros to
    /// <paramref name="padded"/> bytes and converts them to the kernel's layout (if any).
    /// </summary>
    private static void ReadBatch<T>(IRegionSource sources, IPreparedKernel<T>? kernel, int first, int count, long offset, int len, int padded, int stride, byte* buffer)
        where T : IEquatable<T>
    {
        for (int i = 0; i < count; i++)
        {
            byte* input = buffer + (long)i * stride;
            sources.Read(first + i, offset, new Span<byte>(input, len));
            new Span<byte>(input + len, padded - len).Clear();
            kernel?.Prepare(input, padded);
        }
    }

    /// <summary>
    /// Slices, chunks and the lengths given to the kernels are multiples of this: of the symbol size, of a cache line
    /// and of every prepared kernel's block (at most 128 bytes, AVX-512's).
    /// </summary>
    private static long Alignment(int symbolSize) => LeastCommonMultiple(128, symbolSize);

    private static long RoundUp(long v, long a) => (v + a - 1) / a * a;

    private static long LeastCommonMultiple(long a, long b)
    {
        long x = a, y = b;
        while (y != 0)
        {
            (x, y) = (y, x % y);
        }

        return a / x * b;
    }
}

/// <summary>Region source/sink backed by in-memory arrays (tests, small data, matrix-vector steps).</summary>
public sealed class MemoryRegions : IRegionSource, IConcurrentRegionSink
{
    public MemoryRegions(IReadOnlyList<byte[]> regions) => Regions = regions;

    public IReadOnlyList<byte[]> Regions { get; }

    /// <summary>Copies what the region has at <paramref name="offset"/>; the rest (all of it past the end) is zero.</summary>
    public void Read(int index, long offset, Span<byte> destination)
    {
        var r = Regions[index];
        int start = (int)Math.Min(offset, r.Length);
        int available = Math.Min(r.Length - start, destination.Length);
        r.AsSpan(start, available).CopyTo(destination);
        destination[available..].Clear();
    }

    /// <summary>Stores the part of <paramref name="source"/> that fits in the region; bytes past its end are dropped.</summary>
    public void Write(int index, long offset, ReadOnlySpan<byte> source)
    {
        var r = Regions[index];
        int start = (int)Math.Min(offset, r.Length);
        int n = Math.Min(source.Length, r.Length - start);
        source[..n].CopyTo(r.AsSpan(start));
    }
}

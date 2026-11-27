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

    /// <summary>Inputs fused into one pass over each output (more = less memory traffic).</summary>
    public int SourcesPerBatch { get; init; } = 16;

    public IProgress<double>? Progress { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Computes <c>output[j] = Σ_i coef(j, i) · input[i]</c> over byte regions, multithreaded, with fused multi-source
/// SIMD kernels, double-buffered input reads and a memory budget (large outputs are processed in slices).
/// </summary>
public static unsafe class LinearCombiner
{
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

        byte* outputs = (byte*)NativeMemory.AlignedAlloc((nuint)((long)outputCount * sliceLen), 64);
        byte* inA = (byte*)NativeMemory.AlignedAlloc((nuint)((long)batch * sliceLen), 64);
        byte* inB = (byte*)NativeMemory.AlignedAlloc((nuint)((long)batch * sliceLen), 64);
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
                int lenAligned = (int)RoundUp(len, sym);

                if (inputCount == 0)
                {
                    new Span<byte>(outputs, outputCount * sliceLen).Clear();
                }

                // Prime the first batch.
                int firstCount = Math.Min(batch, inputCount);
                ReadBatch(sources, 0, firstCount, offset, lenAligned, sliceLen, inA);

                for (int b = 0; b < batches && inputCount > 0; b++)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    int first = b * batch;
                    int count = Math.Min(batch, inputCount - first);
                    byte* cur = (b & 1) == 0 ? inA : inB;
                    byte* next = (b & 1) == 0 ? inB : inA;

                    // Read the next batch while this one is being combined.
                    Task? reader = null;
                    if (b + 1 < batches)
                    {
                        int nFirst = first + batch;
                        int nCount = Math.Min(batch, inputCount - nFirst);
                        nint nextPtr = (nint)next;
                        long off = offset;
                        reader = Task.Run(() => ReadBatch(sources, nFirst, nCount, off, lenAligned, sliceLen, (byte*)nextPtr));
                    }

                    nint curPtr = (nint)cur;
                    nint outPtr = (nint)outputs;
                    bool overwrite = b == 0;
                    Parallel.For(0, outputCount, parallel,
                        () => (coefs: new T[batch], ptrs: (nint)NativeMemory.Alloc((nuint)(batch * sizeof(nint)))),
                        (j, _, state) =>
                        {
                            var coefSpan = state.coefs.AsSpan(0, count);
                            coefficients.Fill(j, first, coefSpan);
                            byte** ptrs = (byte**)state.ptrs;
                            int used = 0;
                            for (int i = 0; i < count; i++)
                            {
                                if (!field.IsZero(coefSpan[i]))
                                {
                                    coefSpan[used] = coefSpan[i];
                                    ptrs[used] = (byte*)curPtr + (long)i * sliceLen;
                                    used++;
                                }
                            }

                            byte* dst = (byte*)outPtr + (long)j * sliceLen;
                            if (used == 0)
                            {
                                if (overwrite)
                                {
                                    new Span<byte>(dst, lenAligned).Clear();
                                }

                                return state;
                            }

                            using var tables = field.CreateTables(coefSpan[..used]);
                            field.MulAddMulti(tables, 0, used, ptrs, dst, lenAligned, overwrite);
                            return state;
                        },
                        state => NativeMemory.Free((void*)state.ptrs));

                    reader?.Wait(options.CancellationToken);
                    stepsDone++;
                    options.Progress?.Report((double)stepsDone / totalSteps);
                }

                for (int j = 0; j < outputCount; j++)
                {
                    sinks.Write(j, offset, new ReadOnlySpan<byte>(outputs + (long)j * sliceLen, len));
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(outputs);
            NativeMemory.AlignedFree(inA);
            NativeMemory.AlignedFree(inB);
        }
    }

    /// <summary>
    /// Length of the region slices processed per pass: all outputs plus two input batches must fit in the memory
    /// budget. A multiple of 64 bytes and of the symbol size. When it is ≥ the region length, everything is done in
    /// one pass and every input byte is read exactly once (in order), which callers use to hash while encoding.
    /// </summary>
    public static int PlanSliceLength(int symbolSize, int inputCount, int outputCount, long regionLength, LinearCombineOptions options)
    {
        int batch = Math.Max(1, Math.Min(options.SourcesPerBatch, Math.Max(1, inputCount)));
        long align = LeastCommonMultiple(64, symbolSize);
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

    private static void ReadBatch(IRegionSource sources, int first, int count, long offset, int len, int stride, byte* buffer)
    {
        for (int i = 0; i < count; i++)
        {
            sources.Read(first + i, offset, new Span<byte>(buffer + (long)i * stride, len));
        }
    }

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
public sealed class MemoryRegions : IRegionSource, IRegionSink
{
    public MemoryRegions(IReadOnlyList<byte[]> regions) => Regions = regions;

    public IReadOnlyList<byte[]> Regions { get; }

    public void Read(int index, long offset, Span<byte> destination)
    {
        var r = Regions[index];
        int available = (int)Math.Clamp(r.Length - offset, 0, destination.Length);
        r.AsSpan((int)offset, available).CopyTo(destination);
        destination[available..].Clear();
    }

    public void Write(int index, long offset, ReadOnlySpan<byte> source)
    {
        var r = Regions[index];
        int n = (int)Math.Min(source.Length, r.Length - offset);
        source[..n].CopyTo(r.AsSpan((int)offset));
    }
}

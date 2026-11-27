using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DeveParTools.Core.Codes.Fft;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;

namespace DeveParTools;

/// <summary>One measured throughput.</summary>
/// <param name="Key">Stable machine-readable name, e.g. <c>gf16.Avx2</c> or <c>blake3.threads</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="GigabytesPerSecond">Bytes of input processed per second, in units of 10^9.</param>
public sealed record ParBenchmarkResult(string Key, string Name, double GigabytesPerSecond);

/// <summary>Options for <see cref="ParBenchmark.Run"/>.</summary>
public sealed record ParBenchmarkOptions
{
    /// <summary>Buffer size for the single-threaded kernels.</summary>
    public int BufferSize { get; init; } = 256 * 1024;

    /// <summary>Time spent per item: the first third warms up the JIT, the rest is measured.</summary>
    public TimeSpan TimePerItem { get; init; } = TimeSpan.FromMilliseconds(450);

    /// <summary>Called after each item, on the benchmark thread.</summary>
    public Action<ParBenchmarkResult>? Progress { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Measures the hot paths on this machine: GF multiply-add for every supported SIMD level, the hashes, and FFT
/// Reed-Solomon encoding. The CLI's <c>bench</c> command and the app's benchmark page both use this.
/// </summary>
public static unsafe class ParBenchmark
{
    public static IReadOnlyList<ParBenchmarkResult> Run(ParBenchmarkOptions? options = null)
    {
        options ??= new ParBenchmarkOptions();
        int size = Math.Max(4096, options.BufferSize / 128 * 128);
        var src = new byte[size];
        var dst = new byte[size];
        new Random(1).NextBytes(src);
        var results = new List<ParBenchmarkResult>();

        void Add(string key, string name, long bytes, Action action)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var r = new ParBenchmarkResult(key, name, Measure(action, bytes, options.TimePerItem));
            results.Add(r);
            options.Progress?.Invoke(r);
        }

        foreach (var level in SimdSupport.SupportedLevels)
        {
            using var t16 = Gf16MulTables.Create(GaloisField16.Par2, [0x1234, 0x4321, 0x9999, 0x0101], level);
            Add($"gf16.{level}", $"GF(2^16) multiply-add · {level}", 4L * size, () => MulAdd16(t16, src, dst));
        }

        foreach (var level in SimdSupport.SupportedLevels)
        {
            using var t8 = Gf8MulTables.Create(GaloisField8.Default, [0x12, 0x43, 0x99, 0x01], level);
            Add($"gf8.{level}", $"GF(2^8) multiply-add · {level}", 4L * size, () => MulAdd8(t8, src, dst));
        }

        var digest = new byte[32];
        Add("blake3", "BLAKE3 (1 thread)", size, () => Blake3.Hash(src, digest));
        var big = new byte[16 << 20];
        new Random(2).NextBytes(big);
        Add("blake3.threads", "BLAKE3 (all cores)", big.Length, () => Blake3.Hash(big, digest));
        Add("crc64", "CRC-64-ISO", size, () => Crc64Iso.Compute(src));
        Add("crc32", "CRC-32", size, () => Crc32Ieee.Compute(src));
        Add("md5", "MD5", size, () => MD5.HashData(src, digest));

        // FFT Reed-Solomon: 1000 originals and 100 recovery blocks of 4 KiB, on all cores.
        const int k = 1000, r = 100, bytes = 4096;
        var buffers = new nint[k + LeopardCodec.EncodeWorkCount(k, r)];
        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = (nint)NativeMemory.AlignedAlloc(bytes, 64);
                new Span<byte>((void*)buffers[i], bytes).Fill((byte)i);
            }

            var originals = buffers[..k];
            var work = buffers[k..];
            Add("fft.encode", "FFT Reed-Solomon encode (all cores)", (long)k * bytes, () => LeopardCodec.Encode(LeopardField.FF16, originals, r, work, bytes, Environment.ProcessorCount));
        }
        finally
        {
            foreach (var p in buffers)
            {
                NativeMemory.AlignedFree((void*)p);
            }
        }

        return results;
    }

    private static void MulAdd16(Gf16MulTables tables, byte[] src, byte[] dst)
    {
        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            byte** sources = stackalloc byte*[4] { s, s, s, s };
            Gf16Kernels.MulAddMulti(tables, 0, 4, sources, d, src.Length);
        }
    }

    private static void MulAdd8(Gf8MulTables tables, byte[] src, byte[] dst)
    {
        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            byte** sources = stackalloc byte*[4] { s, s, s, s };
            Gf8Kernels.MulAddMulti(tables, 0, 4, sources, d, src.Length);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> for the first third of <paramref name="time"/> without timing it. This gives the
    /// tiered JIT time to swap in fully optimised code. Then it takes the best of five timed slices.
    /// </summary>
    internal static double Measure(Action action, long bytes, TimeSpan time)
    {
        var warmup = Stopwatch.StartNew();
        do
        {
            action();
        }
        while (warmup.Elapsed < time / 3);

        var slice = time * 2 / 3 / 5;
        double best = double.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            int iterations = 0;
            do
            {
                action();
                iterations++;
            }
            while (sw.Elapsed < slice);
            best = Math.Min(best, sw.Elapsed.TotalSeconds / iterations);
        }

        return bytes / best / 1e9;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using DeveParTools.Core.Codes.Fft;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;

namespace DeveParTools.Cli;

/// <summary>Quick throughput measurements of the hot paths on this machine.</summary>
internal static unsafe class Bench
{
    public static void Run(Common c, int size)
    {
        size = Math.Max(4096, size / 128 * 128);
        var src = new byte[size];
        var dst = new byte[size];
        new Random(1).NextBytes(src);
        var results = new Dictionary<string, object?>();
        var lines = new List<string> { $"Buffer {size / 1024} KiB, single thread. CPU: {SimdSupport.Describe()}" };

        foreach (var level in SimdSupport.SupportedLevels)
        {
            double gf16 = Measure(() =>
            {
                using var t = Gf16MulTables.Create(GaloisField16.Par2, [0x1234, 0x4321, 0x9999, 0x0101], level);
                fixed (byte* s = src)
                fixed (byte* d = dst)
                {
                    byte** ptrs = stackalloc byte*[4];
                    for (int i = 0; i < 4; i++)
                    {
                        ptrs[i] = s;
                    }

                    Gf16Kernels.MulAddMulti(t, 0, 4, ptrs, d, size);
                }
            }, 4L * size);
            double gf8 = Measure(() =>
            {
                using var t = Gf8MulTables.Create(GaloisField8.Default, [0x12, 0x43, 0x99, 0x01], level);
                fixed (byte* s = src)
                fixed (byte* d = dst)
                {
                    byte** ptrs = stackalloc byte*[4];
                    for (int i = 0; i < 4; i++)
                    {
                        ptrs[i] = s;
                    }

                    Gf8Kernels.MulAddMulti(t, 0, 4, ptrs, d, size);
                }
            }, 4L * size);
            results[$"gf16.{level}"] = gf16;
            results[$"gf8.{level}"] = gf8;
            lines.Add($"GF(2^16) multiply-add {level,-8}: {gf16,8:F2} GB/s    GF(2^8) {level,-8}: {gf8,8:F2} GB/s");
        }

        double blake = Measure(() => Blake3.Hash(src, dst.AsSpan(0, 32)), size);
        double crc64 = Measure(() => Crc64Iso.Compute(src), size);
        double crc32 = Measure(() => Crc32Ieee.Compute(src), size);
        double md5 = Measure(() => System.Security.Cryptography.MD5.HashData(src), size);
        results["blake3"] = blake;
        results["crc64"] = crc64;
        results["crc32"] = crc32;
        results["md5"] = md5;
        lines.Add($"BLAKE3 {blake:F2} GB/s, CRC-64-ISO {crc64:F2} GB/s, CRC-32 {crc32:F2} GB/s, MD5 {md5:F2} GB/s");

        // FFT Reed-Solomon: 1000 originals, 100 recovery blocks of 4 KiB.
        const int k = 1000, r = 100, bytes = 4096;
        var orig = new nint[k];
        var work = new nint[LeopardCodec.EncodeWorkCount(k, r)];
        for (int i = 0; i < k; i++)
        {
            orig[i] = (nint)NativeMemory.AlignedAlloc(bytes, 64);
            new Span<byte>((void*)orig[i], bytes).Fill((byte)i);
        }

        for (int i = 0; i < work.Length; i++)
        {
            work[i] = (nint)NativeMemory.AlignedAlloc(bytes, 64);
        }

        double fft = Measure(() => LeopardCodec.Encode(LeopardField.FF16, orig, r, work, bytes, Environment.ProcessorCount), (long)k * bytes);
        foreach (var p in orig.Concat(work))
        {
            NativeMemory.AlignedFree((void*)p);
        }

        results["fftEncode"] = fft;
        lines.Add($"FFT Reed-Solomon encode (1000+100 blocks, all cores): {fft:F2} GB/s of input");
        c.Result("bench", results, string.Join('\n', lines));
    }

    /// <summary>Best-of-5 throughput in GB/s of <paramref name="action"/> processing <paramref name="bytes"/>.</summary>
    private static double Measure(Action action, long bytes)
    {
        action();
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
            while (sw.ElapsedMilliseconds < 60);
            best = Math.Min(best, sw.Elapsed.TotalSeconds / iterations);
        }

        return bytes / best / 1e9;
    }
}

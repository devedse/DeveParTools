using DeveParTools.Core.Galois;

namespace DeveParTools.Tests;

[TestClass]
public sealed class ParBenchmarkTests
{
    [TestMethod]
    public void Run_MeasuresEveryItem_AndReportsProgress()
    {
        var progress = new List<ParBenchmarkResult>();
        var results = ParBenchmark.Run(new ParBenchmarkOptions
        {
            BufferSize = 1000, // rounded up to the 4 KiB minimum
            TimePerItem = TimeSpan.FromMilliseconds(6),
            Progress = progress.Add,
        });

        CollectionAssert.AreEqual(results.ToList(), progress);
        var keys = results.Select(r => r.Key).ToList();
        foreach (var level in SimdSupport.SupportedLevels)
        {
            CollectionAssert.Contains(keys, $"gf16.{level}");
            CollectionAssert.Contains(keys, $"gf8.{level}");
        }

        foreach (var key in new[] { "blake3", "blake3.threads", "crc64", "crc32", "md5", "fft.encode" })
        {
            CollectionAssert.Contains(keys, key);
        }

        Assert.IsTrue(results.All(r => r.GigabytesPerSecond > 0 && double.IsFinite(r.GigabytesPerSecond)));
        Assert.AreEqual(keys.Count, keys.Distinct().Count());
    }

    [TestMethod]
    public void Run_CanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => ParBenchmark.Run(new ParBenchmarkOptions { CancellationToken = cts.Token }));
    }

    [TestMethod]
    public void Measure_ComputesBytesPerSecond()
    {
        int calls = 0;
        double gbps = ParBenchmark.Measure(() =>
        {
            calls++;
            Thread.Sleep(1);
        }, 1_000_000, TimeSpan.FromMilliseconds(30));

        // Each call takes at least 1 ms, so 1 MB per call is at most 1 GB/s.
        Assert.IsTrue(gbps is > 0 and <= 1.0, gbps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsTrue(calls >= 6, "one warm-up call and at least one call per timed slice");
    }
}

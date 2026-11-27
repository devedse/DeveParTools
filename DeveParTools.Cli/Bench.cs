using DeveParTools.Core.Operations;
using DeveParTools.Core.Galois;

namespace DeveParTools.Cli;

/// <summary>The <c>bench</c> command: runs <see cref="ParBenchmark"/> and prints one line per measurement.</summary>
internal static class Bench
{
    public static void Run(Common c, int size, TimeSpan timePerItem)
    {
        c.Log(ParLogLevel.Info, $"CPU: {SimdSupport.Describe()}, {Environment.ProcessorCount} threads. Single-threaded items use a {size / 1024} KiB buffer.");
        var results = ParBenchmark.Run(new ParBenchmarkOptions
        {
            BufferSize = size,
            TimePerItem = timePerItem,
            Progress = r => c.Log(ParLogLevel.Info, $"{r.Name,-42} {r.GigabytesPerSecond,8:F2} GB/s"),
        });
        c.Result("bench", results.ToDictionary(r => r.Key, r => (object?)Math.Round(r.GigabytesPerSecond, 3)), "Done.");
    }
}

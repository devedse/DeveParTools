using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Codes;

/// <summary><see cref="LinearCombiner"/> edge cases; the main reference comparisons are in <see cref="MatrixAndCombinerTests"/>.</summary>
[TestClass]
public sealed class LinearCombinerTests
{
    private static readonly Gf8Field F8 = new(GaloisField8.Default);

    [TestMethod]
    public void NoOutputsOrEmptyRegions_DoNothing()
    {
        var sink = new RecordingSink();
        var source = new MemoryRegions([new byte[10]]);
        var coef = new MatrixCoefficients<byte>(new GfMatrix<byte>(1, 1, F8));
        LinearCombiner.Combine(F8, coef, 1, 0, source, sink, 10);
        LinearCombiner.Combine(F8, coef, 1, 1, source, sink, 0);
        Assert.AreEqual(0, sink.Writes);
    }

    [TestMethod]
    public void NoInputs_GiveZeroOutputs()
    {
        var outputs = new[] { Filled(100, 0xAA), Filled(100, 0x55) };
        LinearCombiner.Combine(F8, new MatrixCoefficients<byte>(new GfMatrix<byte>(2, 0, F8)), 0, 2, new MemoryRegions([]), new MemoryRegions(outputs), 100,
            new LinearCombineOptions { MemoryBudget = 64 }); // several slices
        Assert.IsTrue(outputs.All(o => o.All(b => b == 0)));
    }

    [TestMethod]
    public void ZeroCoefficientBatches_AreSkipped()
    {
        // Output 0 has only zero coefficients (cleared in the first batch), output 1 has zeros in the second batch
        // only (nothing is added there), output 2 uses everything.
        var rnd = new Random(1);
        int inputs = 6, length = 300;
        var inData = Enumerable.Range(0, inputs).Select(_ => TestData.RandomBytes(rnd, length)).ToArray();
        var m = new GfMatrix<byte>(3, inputs, F8);
        for (int i = 0; i < inputs; i++)
        {
            m[1, i] = i < 3 ? (byte)(i + 1) : (byte)0;
            m[2, i] = (byte)(10 + i);
        }

        var outData = Enumerable.Range(0, 3).Select(_ => Filled(length, 0xEE)).ToArray();
        LinearCombiner.Combine(F8, new MatrixCoefficients<byte>(m), inputs, 3, new MemoryRegions(inData), new MemoryRegions(outData), length,
            new LinearCombineOptions { SourcesPerBatch = 3, Threads = 2 });
        for (int j = 0; j < 3; j++)
        {
            var expected = new byte[length];
            for (int i = 0; i < inputs; i++)
            {
                Gf8Kernels.MulAdd(GaloisField8.Default, m[j, i], inData[i], expected);
            }

            CollectionAssert.AreEqual(expected, outData[j], $"output {j}");
        }
    }

    [TestMethod]
    public void ShortInputsAndOddLengths_ArePaddedWithZeros()
    {
        // Region length not a multiple of the symbol size (GF(2^16)) and inputs shorter than the region.
        var f = new Gf16Field(GaloisField16.Par2);
        var a = new byte[] { 1, 2, 3, 4, 5 };
        var b = new byte[] { 7 };
        var m = new GfMatrix<ushort>(1, 2, f);
        m[0, 0] = 3;
        m[0, 1] = 0x100;
        var output = Filled(7, 0x99);
        LinearCombiner.Combine(f, new MatrixCoefficients<ushort>(m), 2, 1, new MemoryRegions([a, b]), new MemoryRegions([output]), 7);
        var pa = new byte[] { 1, 2, 3, 4, 5, 0, 0, 0 };
        var pb = new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 };
        var expected = new byte[8];
        Gf16Kernels.MulAdd(GaloisField16.Par2, 3, pa, expected);
        Gf16Kernels.MulAdd(GaloisField16.Par2, 0x100, pb, expected);
        CollectionAssert.AreEqual(expected[..7], output);
    }

    [TestMethod]
    public void Progress_ReachesOne_AndCancellationStops()
    {
        var rnd = new Random(2);
        var inData = Enumerable.Range(0, 4).Select(_ => TestData.RandomBytes(rnd, 256)).ToArray();
        var m = new GfMatrix<byte>(2, 4, F8);
        rnd.NextBytes(m.Data);
        var reports = new List<double>();
        var progress = new SyncProgress(reports.Add);
        LinearCombiner.Combine(F8, new MatrixCoefficients<byte>(m), 4, 2, new MemoryRegions(inData), new MemoryRegions([new byte[256], new byte[256]]), 256,
            new LinearCombineOptions { SourcesPerBatch = 2, MemoryBudget = 64 * 8, Progress = progress });
        Assert.IsTrue(reports.Count > 1);
        Assert.AreEqual(1.0, reports[^1], 1e-12);
        CollectionAssert.AreEqual(reports.Order().ToList(), reports, "progress never goes back");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => LinearCombiner.Combine(F8, new MatrixCoefficients<byte>(m), 4, 2, new MemoryRegions(inData),
            new MemoryRegions([new byte[256], new byte[256]]), 256, new LinearCombineOptions { CancellationToken = cts.Token }));
    }

    /// <summary>
    /// Regression: when combining was cancelled while the next batch was still being read in the background,
    /// <see cref="LinearCombiner.Combine{T}"/> freed the input buffers and returned while that read kept writing into
    /// them. It now waits for the read first.
    /// </summary>
    [TestMethod]
    public void Cancellation_WaitsForTheBackgroundRead()
    {
        using var cts = new CancellationTokenSource();
        var inData = new[] { Filled(64, 1), Filled(64, 2) };
        var source = new SlowSecondInput(inData, cts.Token);
        var m = new GfMatrix<byte>(1, 2, F8);
        m[0, 0] = m[0, 1] = 1;
        var coefficients = new CancellingCoefficients(new MatrixCoefficients<byte>(m), cts);
        Assert.Throws<OperationCanceledException>(() => LinearCombiner.Combine(F8, coefficients, 2, 1, source, new MemoryRegions([new byte[64]]), 64,
            new LinearCombineOptions { SourcesPerBatch = 1, CancellationToken = cts.Token }));
        int stillReading = source.ActiveReads;
        source.Abandoned = true;
        Assert.AreEqual(0, stillReading, "a background read was still writing into the freed input buffers");
    }

    [TestMethod]
    public void PlanSliceLength_IsAlignedAndBounded()
    {
        var options = new LinearCombineOptions { MemoryBudget = 1 << 20, SourcesPerBatch = 16 };
        foreach (int sym in new[] { 1, 2, 3, 16, 24 })
        {
            int slice = LinearCombiner.PlanSliceLength(sym, 100, 10, 1L << 30, options);
            Assert.AreEqual(0, slice % 64, $"sym={sym}");
            Assert.AreEqual(0, slice % sym, $"sym={sym}");
            Assert.IsTrue(slice > 0 && slice <= (1 << 20) / (10 + 2 * 16), $"sym={sym}: fits the budget");
        }

        // Small regions are done in one slice, rounded up to the alignment.
        Assert.AreEqual(64, LinearCombiner.PlanSliceLength(2, 3, 3, 10, options));
        Assert.AreEqual(192, LinearCombiner.PlanSliceLength(3, 3, 3, 10, options));

        // A tiny budget still gives one aligned unit.
        Assert.AreEqual(64, LinearCombiner.PlanSliceLength(1, 100, 100, 1L << 30, new LinearCombineOptions { MemoryBudget = 1 }));

        // A huge budget is capped so the slice stays addressable as an int span.
        int capped = LinearCombiner.PlanSliceLength(1, 1, 1, long.MaxValue / 4, new LinearCombineOptions { MemoryBudget = long.MaxValue });
        Assert.AreEqual(int.MaxValue / 2 / 64 * 64, capped);
    }

    [TestMethod]
    public void MemoryRegions_ReadPadsAndWriteClips()
    {
        var r = new MemoryRegions([new byte[] { 1, 2, 3 }]);
        var buf = Filled(5, 9);
        r.Read(0, 1, buf);
        CollectionAssert.AreEqual(new byte[] { 2, 3, 0, 0, 0 }, buf);
        r.Read(0, 10, buf);
        CollectionAssert.AreEqual(new byte[5], buf, "entirely past the end");
        r.Write(0, 2, new byte[] { 7, 8, 9 });
        CollectionAssert.AreEqual(new byte[] { 1, 2, 7 }, r.Regions[0], "only the part that fits is written");
        r.Write(0, 10, new byte[] { 4, 5 });
        CollectionAssert.AreEqual(new byte[] { 1, 2, 7 }, r.Regions[0], "entirely past the end: nothing is written");
    }

    /// <summary>
    /// Regression: <see cref="MemoryRegions"/> threw when asked for bytes entirely past the end of a region, which
    /// happens as soon as an input is shorter than the region by more than one slice.
    /// </summary>
    [TestMethod]
    public void InputShorterThanSeveralSlices_IsZeroPadded()
    {
        var rnd = new Random(3);
        var longInput = TestData.RandomBytes(rnd, 500);
        var shortInput = TestData.RandomBytes(rnd, 10);
        var m = new GfMatrix<byte>(2, 2, F8);
        m[0, 0] = 3;
        m[0, 1] = 5;
        m[1, 1] = 1;
        var outputs = new[] { new byte[500], new byte[20] }; // output 1 is shorter than the region: clipped
        LinearCombiner.Combine(F8, new MatrixCoefficients<byte>(m), 2, 2, new MemoryRegions([longInput, shortInput]), new MemoryRegions(outputs), 500,
            new LinearCombineOptions { MemoryBudget = 64 * 6 }); // 64-byte slices: 8 passes
        var padded = new byte[500];
        shortInput.CopyTo(padded, 0);
        var expected = new byte[500];
        Gf8Kernels.MulAdd(GaloisField8.Default, 3, longInput, expected);
        Gf8Kernels.MulAdd(GaloisField8.Default, 5, padded, expected);
        CollectionAssert.AreEqual(expected, outputs[0]);
        CollectionAssert.AreEqual(padded[..20], outputs[1]);
    }

    private static byte[] Filled(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private sealed class RecordingSink : IRegionSink
    {
        public int Writes { get; private set; }

        public void Write(int index, long offset, ReadOnlySpan<byte> source) => Writes++;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class CancellingCoefficients(ICoefficientProvider<byte> inner, CancellationTokenSource cts) : ICoefficientProvider<byte>
    {
        public void Fill(int output, int firstInput, Span<byte> destination)
        {
            inner.Fill(output, firstInput, destination);
            cts.Cancel();
        }
    }

    /// <summary>Input 1 (read in the background) only completes a while after cancellation.</summary>
    private sealed class SlowSecondInput(byte[][] data, CancellationToken cancelled) : IRegionSource
    {
        private int _activeReads;

        public int ActiveReads => Volatile.Read(ref _activeReads);

        /// <summary>Set once Combine has returned: a late read must then not touch the (freed) destination.</summary>
        public volatile bool Abandoned;

        public void Read(int index, long offset, Span<byte> destination)
        {
            Interlocked.Increment(ref _activeReads);
            try
            {
                if (index == 1)
                {
                    cancelled.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                    Thread.Sleep(200);
                }

                if (!Abandoned)
                {
                    new MemoryRegions(data).Read(index, offset, destination);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }
    }
}

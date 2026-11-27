using DeveParTools.Core.Codes;
using DeveParTools.Core.Codes.Fft;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>The FFT (Leopard) code with cohorts, on in-memory blocks.</summary>
[TestClass]
public sealed class FftTests
{
    private sealed class Memory(byte[][] blocks) : IRegionSource, IRegionSink
    {
        public byte[][] Blocks { get; } = blocks;

        public void Read(int index, long offset, Span<byte> destination)
        {
            destination.Clear();
            var b = Blocks[index];
            if (offset < b.Length)
            {
                b.AsSpan((int)offset, (int)Math.Min(destination.Length, b.Length - offset)).CopyTo(destination);
            }
        }

        public void Write(int index, long offset, ReadOnlySpan<byte> source) => source.CopyTo(Blocks[index].AsSpan((int)offset));
    }

    private static byte[][] Blocks(int count, int size, int seed) => Enumerable.Range(0, count).Select(i => TestData.Random(seed + i, size)).ToArray();

    private static byte[][] Empty(int count, int size) => Enumerable.Range(0, count).Select(_ => new byte[size]).ToArray();

    private static LinearCombineOptions Options(long budget = 1 << 20) => new() { MemoryBudget = budget, Threads = 2 };

    [TestMethod]
    public void Plan_SelectsXorFf8OrFf16LikePar3cmdline()
    {
        // One recovery block and one cohort: plain XOR.
        Assert.AreEqual(new Par3Fft.Layout(1, 10, 1, null), Par3Fft.Plan(10, 1, 1));

        // m = NextPow2(ceil(R / C)), n = NextPow2(m + ceil(k / C)): FF8 when n ≤ 256.
        var small = Par3Fft.Plan(100, 20, 1);
        Assert.AreEqual((1, 100, 20, 32), (small.Cohorts, small.OriginalsPerCohort, small.RecoveryPerCohort, small.M));
        Assert.AreEqual(LeopardField.FF8, small.Field);
        Assert.AreEqual(LeopardField.FF16, Par3Fft.Plan(250, 20, 1).Field);

        // Two cohorts halve both counts; one recovery block per cohort is still a real code (not XOR).
        var two = Par3Fft.Plan(251, 2, 2);
        Assert.AreEqual((2, 126, 1, 1), (two.Cohorts, two.OriginalsPerCohort, two.RecoveryPerCohort, two.M));
        Assert.AreEqual(LeopardField.FF8, two.Field);
    }

    [TestMethod]
    public void ChooseCohorts_SmallestCountThatFitsGf16()
    {
        Assert.AreEqual(1, Par3Fft.ChooseCohorts(1000, 100));
        Assert.AreEqual(1, Par3Fft.ChooseCohorts(65536 - 1024, 1024));
        Assert.AreEqual(2, Par3Fft.ChooseCohorts(65536, 1024));
        Assert.AreEqual(4, Par3Fft.ChooseCohorts(200_000, 100));

        // More recovery than input blocks never fits (m ≤ k per cohort).
        Assert.ThrowsExactly<ArgumentException>(() => Par3Fft.ChooseCohorts(1 << 20, 1 << 21));
    }

    [TestMethod]
    public void FromPacket_ReadsTheMatrixPacket()
    {
        var p = new Par3FftMatrixPacket(0, 0, 3, 2);
        Assert.AreEqual(new Par3Fft.Layout(3, 4, 8, LeopardField.FF8), Par3Fft.FromPacket(p, 1, 10));
        Assert.AreEqual(LeopardField.FF16, Par3Fft.FromPacket(p, 2, 10)!.Field);
        Assert.AreEqual(new Par3Fft.Layout(1, 10, 1, null), Par3Fft.FromPacket(p, 0, 10));

        // Parameters Leopard cannot handle.
        Assert.IsNull(Par3Fft.FromPacket(p, 4, 10));
        Assert.IsNull(Par3Fft.FromPacket(p with { MaxRecoveryLog2 = 16 }, 2, 10));
        Assert.IsNull(Par3Fft.FromPacket(p with { Interleave = uint.MaxValue }, 2, 10));
    }

    [TestMethod]
    public void CanDecode_IsPerCohort()
    {
        var layout = new Par3Fft.Layout(2, 3, 2, LeopardField.FF8);
        var available = new[] { true, false, true, false, true, true };

        // Blocks 1 and 3 are both in cohort 1; recovery 0 and 2 are both cohort 0.
        Assert.IsFalse(Par3Fft.CanDecode(layout, 6, available, [0, 2]));
        Assert.IsTrue(Par3Fft.CanDecode(layout, 6, available, [1, 3]));

        // Local index r / C must be below m = 2: recovery 5 is local 2 of cohort 1, unusable.
        Assert.IsFalse(Par3Fft.CanDecode(layout, 6, available, [1, 5]));

        var xor = new Par3Fft.Layout(1, 6, 1, null);
        Assert.IsTrue(Par3Fft.CanDecode(xor, 6, [true, true, true, true, true, true], []));
        Assert.IsTrue(Par3Fft.CanDecode(xor, 6, [true, false, true, true, true, true], [0]));
        Assert.IsFalse(Par3Fft.CanDecode(xor, 6, [true, false, true, true, true, true], [1]));
        Assert.IsFalse(Par3Fft.CanDecode(xor, 6, available, [0]));
    }

    [TestMethod]
    public void Xor_EncodeAndDecodeOneMissingBlock()
    {
        var data = Blocks(5, 100, 1);
        var layout = Par3Fft.Plan(5, 1, 1);
        var recovery = new Memory(Empty(1, 100));
        Par3Fft.Encode(layout, 5, [0], new Memory(data), recovery, 100, Options());
        var expected = new byte[100];
        foreach (var b in data)
        {
            for (int i = 0; i < 100; i++)
            {
                expected[i] ^= b[i];
            }
        }

        CollectionAssert.AreEqual(expected, recovery.Blocks[0]);

        var damaged = data.Select(b => (byte[])b.Clone()).ToArray();
        Array.Clear(damaged[3]);
        var output = new Memory(Empty(5, 100));
        Par3Fft.Decode(layout, 5, [true, true, true, false, true], new Dictionary<ulong, int> { [0] = 0 }, new Memory(damaged), recovery, output, 100, Options());
        CollectionAssert.AreEqual(data[3], output.Blocks[3]);
    }

    [TestMethod]
    [DataRow(1, 20, 6, 4096L, DisplayName = "1 cohort, FF8")]
    [DataRow(3, 31, 9, 4096L, DisplayName = "3 cohorts, last cohort short (31 = 3·11 − 2)")]
    [DataRow(2, 600, 40, 4096L, DisplayName = "2 cohorts, FF16")]
    [DataRow(1, 20, 6, 200L, DisplayName = "tiny memory budget: many slices")]
    public void Cohorts_EncodeThenDecodeTheWorstCaseLoss(int cohorts, int count, int recoveryCount, long budget)
    {
        const int size = 1000; // not a multiple of 64: slices are padded
        var data = Blocks(count, size, 10);
        var layout = Par3Fft.Plan((ulong)count, recoveryCount, cohorts);
        var indices = Enumerable.Range(0, recoveryCount).Select(i => (ulong)i).ToList();
        var recovery = new Memory(Empty(recoveryCount, size));
        Par3Fft.Encode(layout, (ulong)count, indices, new Memory(data), recovery, size, Options(budget));

        // Lose as many blocks per cohort as it has recovery blocks, including the last (short) cohort.
        var available = Enumerable.Repeat(true, count).ToArray();
        for (int c = 0; c < cohorts; c++)
        {
            int perCohort = indices.Count(r => (int)(r % (ulong)cohorts) == c);
            var members = Enumerable.Range(0, count).Where(i => i % cohorts == c).Reverse().Take(perCohort);
            foreach (var i in members)
            {
                available[i] = false;
            }
        }

        Assert.IsTrue(Par3Fft.CanDecode(layout, (ulong)count, available, indices));
        var damaged = data.Select((b, i) => available[i] ? b : new byte[size]).ToArray();
        var output = new Memory(Empty(count, size));
        var map = indices.ToDictionary(r => r, r => (int)r);
        Par3Fft.Decode(layout, (ulong)count, available, map, new Memory(damaged), recovery, output, size, Options(budget));
        for (int i = 0; i < count; i++)
        {
            if (!available[i])
            {
                CollectionAssert.AreEqual(data[i], output.Blocks[i], $"block {i}");
            }
        }
    }

    [TestMethod]
    public void Encode_OnlyRequestedRecoveryIndices()
    {
        // Recovery indices 1 and 3 of a 2-cohort code: both in cohort 1, so cohort 0 is skipped entirely.
        var data = Blocks(10, 64, 3);
        var layout = Par3Fft.Plan(10, 4, 2);
        var all = new Memory(Empty(4, 64));
        Par3Fft.Encode(layout, 10, [0, 1, 2, 3], new Memory(data), all, 64, Options());
        var some = new Memory(Empty(2, 64));
        Par3Fft.Encode(layout, 10, [1, 3], new Memory(data), some, 64, Options());
        CollectionAssert.AreEqual(all.Blocks[1], some.Blocks[0]);
        CollectionAssert.AreEqual(all.Blocks[3], some.Blocks[1]);
    }

    [TestMethod]
    public void Decode_CohortWithoutEnoughRecovery_Throws()
    {
        var layout = Par3Fft.Plan(6, 2, 2);
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => Par3Fft.Decode(layout, 6, [true, false, true, false, true, true],
            new Dictionary<ulong, int> { [0] = 0, [2] = 1 }, new Memory(Empty(6, 64)), new Memory(Empty(2, 64)), new Memory(Empty(6, 64)), 64, Options()));
        StringAssert.Contains(e.Message, "Cohort 1");
    }

    [TestMethod]
    public void Encode_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var layout = Par3Fft.Plan(4, 2, 1);
        Assert.ThrowsExactly<OperationCanceledException>(() => Par3Fft.Encode(layout, 4, [0, 1], new Memory(Blocks(4, 64, 1)), new Memory(Empty(2, 64)), 64,
            new LinearCombineOptions { CancellationToken = cts.Token }));
    }
}

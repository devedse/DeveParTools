using DeveParTools.Core.Operations;

namespace DeveParTools.Par3.Tests;

/// <summary>The functions that preview a PAR3 set before it is created (used by ParClient.Plan).</summary>
[TestClass]
public sealed class PlanningTests
{
    [TestMethod]
    public void CountBlocks_DocumentedExample()
    {
        // Block size 100: full blocks 2 + 1 + 0; tails 50, 30 (inline), 70 → 70 and 50 do not share a block.
        Assert.AreEqual(5UL, Par3Creator.CountBlocks([250, 130, 70], 100, packTails: true));
        Assert.AreEqual(5UL, Par3Creator.CountBlocks([250, 130, 70], 100, packTails: false));

        // Tails 60 and 40 share one block when packed.
        Assert.AreEqual(1UL, Par3Creator.CountBlocks([60, 40], 100, packTails: true));
        Assert.AreEqual(2UL, Par3Creator.CountBlocks([60, 40], 100, packTails: false));
        Assert.AreEqual(0UL, Par3Creator.CountBlocks([39, 0], 100, packTails: true), "short tails live in the packets");
        Assert.AreEqual(0UL, Par3Creator.CountBlocks([], 100, packTails: true));
    }

    [TestMethod]
    public void FileBlocks_FullBlocksPlusALongTail()
    {
        Assert.AreEqual(3UL, Par3Creator.FileBlocks(250, 100), "2 full blocks + a 50-byte tail");
        Assert.AreEqual(1UL, Par3Creator.FileBlocks(139, 100), "a 39-byte tail is inline");
        Assert.AreEqual(2UL, Par3Creator.FileBlocks(140, 100));
        Assert.AreEqual(0UL, Par3Creator.FileBlocks(0, 100));
    }

    [TestMethod]
    [DataRow(Par3Code.Auto, 200UL, 56, Par3Code.Cauchy, DisplayName = "256 positions fit GF(2^8): Cauchy")]
    [DataRow(Par3Code.Auto, 200UL, 57, Par3Code.Fft, DisplayName = "larger: FFT")]
    [DataRow(Par3Code.Auto, 300UL, 301, Par3Code.Cauchy, DisplayName = "more recovery than input blocks: Cauchy")]
    [DataRow(Par3Code.Fft, 10UL, 1, Par3Code.Fft, DisplayName = "an explicit code stays")]
    [DataRow(Par3Code.Cauchy, 10000UL, 100, Par3Code.Cauchy, DisplayName = "an explicit Cauchy stays")]
    public void ResolveCode(Par3Code code, ulong blocks, int recovery, Par3Code expected) => Assert.AreEqual(expected, Par3Creator.ResolveCode(code, blocks, recovery));

    [TestMethod]
    public void DescribeCode_FieldsAndCohorts()
    {
        Assert.AreEqual("Cauchy Reed-Solomon (GF(2^8))", Par3Creator.DescribeCode(Par3Code.Auto, 100, 10));
        Assert.AreEqual("Cauchy Reed-Solomon (GF(2^16))", Par3Creator.DescribeCode(Par3Code.Cauchy, 1000, 10));
        Assert.AreEqual("Cauchy Reed-Solomon (GF(2^32))", Par3Creator.DescribeCode(Par3Code.Cauchy, 70000, 10));
        Assert.AreEqual("FFT Reed-Solomon (GF(2^16), 1 cohort(s))", Par3Creator.DescribeCode(Par3Code.Auto, 1000, 100));
        Assert.AreEqual("FFT Reed-Solomon (GF(2^8), 1 cohort(s))", Par3Creator.DescribeCode(Par3Code.Fft, 100, 10));
        Assert.AreEqual("FFT Reed-Solomon (XOR, 1 cohort(s))", Par3Creator.DescribeCode(Par3Code.Fft, 100, 1));
        Assert.AreEqual("FFT Reed-Solomon (GF(2^8), 3 cohort(s))", Par3Creator.DescribeCode(Par3Code.Fft, 300, 30, interleave: 2));
        Assert.AreEqual("FFT Reed-Solomon (GF(2^16), 1 cohort(s))", Par3Creator.DescribeCode(Par3Code.Fft, 300, 10, maxRecoveryBlocks: 200), "sized for the maximum");
    }

    [TestMethod]
    public void PlanRecoveryFiles_AbsoluteBlockNumbers()
    {
        var plan = Par3Creator.PlanRecoveryFiles("/x/set.par3", RecoveryFileScheme.Exponential, null, 7, 5, 1024, 10_000);
        CollectionAssert.AreEqual(new[] { "set.vol07+01.par3", "set.vol08+02.par3", "set.vol10+02.par3" }, plan.Select(p => Path.GetFileName(p.Path)).ToArray());
        CollectionAssert.AreEqual(new[] { 7L, 8L, 10L }, plan.Select(p => p.FirstBlock).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1 }, Par3Creator.PlanRecoveryFiles("/x/set.par3", RecoveryFileScheme.LimitSize, null, 0, 5, 1024, 1000).Select(p => p.Count).ToArray(), "a file smaller than a block allows 1 per file");
    }
}

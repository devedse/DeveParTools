using DeveParTools.Core.Operations;

namespace DeveParTools.Core.Tests.Operations;

[TestClass]
public sealed class BlockSizeLimitTests
{
    [TestMethod]
    public void SixtyFourMiB_OrWhat32768BlocksNeed()
    {
        Assert.AreEqual(64L << 20, BlockSizeLimit.For(14L << 30, 8), "14 GiB: 64 MiB blocks, at least 224");
        Assert.AreEqual(64L << 20, BlockSizeLimit.For(0, 4));
        long tenTb = 10_000_000_000_000;
        long limit = BlockSizeLimit.For(tenTb, 8);
        Assert.AreEqual(0, limit % 8);
        Assert.IsTrue(limit * 32768 >= tenTb && (limit - 8) * 32768 < tenTb, "the size that 32,768 blocks need: " + limit);
        StringAssert.Contains(BlockSizeLimit.TooLarge(BlockSizeLimit.Normal), "(64 MiB)");
        StringAssert.Contains(BlockSizeLimit.TooLarge(limit), "what 32,768 blocks need");
    }
}

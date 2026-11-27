using DeveParTools.Core.Operations;

namespace DeveParTools.Core.Tests.Operations;

[TestClass]
public sealed class OperationContextTests
{
    [TestMethod]
    public void Defaults()
    {
        var ctx = OperationContext.Default;
        Assert.AreSame(NullObserver.Instance, ctx.Observer);
        Assert.IsNull(ctx.Blocks);
        Assert.AreEqual(Environment.ProcessorCount, ctx.Threads);
        Assert.AreEqual(CancellationToken.None, ctx.CancellationToken);
        Assert.AreEqual(OperationContext.MemoryBudgetFor(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), ctx.MemoryBudget);
        Assert.AreNotSame(ctx, OperationContext.Default, "Default is a fresh context every time");

        // The null observer accepts everything.
        NullObserver.Instance.Stage("x");
        NullObserver.Instance.Progress(0.5, "detail");
        NullObserver.Instance.Log(ParLogLevel.Error, "message");
    }

    [TestMethod]
    [DataRow(0L, 512L << 20, DisplayName = "unknown → 512 MiB")]
    [DataRow(-1L, 512L << 20, DisplayName = "negative → 512 MiB")]
    [DataRow(128L << 20, 64L << 20, DisplayName = "small machine → at least 64 MiB")]
    [DataRow(16L << 30, 4L << 30, DisplayName = "16 GiB → a quarter")]
    [DataRow(1L << 40, 8L << 30, DisplayName = "huge machine → at most 8 GiB")]
    public void MemoryBudget_IsAQuarterOfTheAvailableMemory_Clamped(long available, long expected)
        => Assert.AreEqual(expected, OperationContext.MemoryBudgetFor(available));

    [TestMethod]
    public void CreateBlockMap_StoresTheMapAndRaisesTheEvent()
    {
        var ctx = new OperationContext();
        var first = ctx.CreateBlockMap(10, 2); // no subscriber yet
        Assert.AreSame(first, ctx.Blocks);

        var seen = new List<BlockStateMap>();
        ctx.BlocksCreated += seen.Add;
        var second = ctx.CreateBlockMap(5, 3);
        Assert.AreSame(second, ctx.Blocks);
        CollectionAssert.AreEqual(new[] { second }, seen);
        Assert.AreEqual(5, second.InputBlockCount);
        Assert.AreEqual(3, second.RecoveryBlockCount);
    }

    [TestMethod]
    public void CombineOptions_ComeFromTheContext()
    {
        using var cts = new CancellationTokenSource();
        var ctx = new OperationContext { Threads = 3, MemoryBudget = 12345, CancellationToken = cts.Token };
        var progress = new Progress<double>();
        var options = ctx.Combine.Create(progress);
        Assert.AreEqual(3, options.Threads);
        Assert.AreEqual(12345, options.MemoryBudget);
        Assert.AreEqual(cts.Token, options.CancellationToken);
        Assert.AreSame(progress, options.Progress);

        var overridden = ctx.Combine.Create(budget: 999);
        Assert.AreEqual(999, overridden.MemoryBudget);
        Assert.IsNull(overridden.Progress);
    }
}

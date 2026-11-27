using DeveParTools.Core.Operations;

namespace DeveParTools.Core.Tests.Operations;

/// <summary>Stage plans, progress helpers and nested operations.</summary>
[TestClass]
public sealed class ProgressReportingTests
{
    private sealed class Recorder : IParOperationObserver
    {
        public List<string> Events { get; } = new();

        public void Plan(IReadOnlyList<OperationStage> stages) => Events.Add("plan " + string.Join(",", stages.Select(s => s.Name)));

        public void Stage(string name) => Events.Add("stage " + name);

        public void Progress(double fraction, string? detail = null) => Events.Add($"progress {fraction:0.00} {detail}");

        public void Log(ParLogLevel level, string message) => Events.Add($"log {level} {message}");
    }

    /// <summary>An observer that does not implement Plan: the interface's default does nothing.</summary>
    private sealed class StagesOnly : IParOperationObserver
    {
        public void Stage(string name)
        {
        }

        public void Progress(double fraction, string? detail = null)
        {
        }

        public void Log(ParLogLevel level, string message)
        {
        }
    }

    [TestMethod]
    public void ToProgress_PassesFractions_OptionallyIntoARange()
    {
        var r = new Recorder();
        r.ToProgress("x").Report(0.5);
        r.ToProgress(0.1, 0.5).Report(0.5);
        r.ToProgress(0.1, 0.5).Report(1);
        CollectionAssert.AreEqual(new[] { "progress 0.50 x", "progress 0.30 ", "progress 0.50 " }, r.Events);
    }

    [TestMethod]
    public void Plan_HasADefaultThatDoesNothing()
    {
        IParOperationObserver o = new StagesOnly();
        o.Plan(VerifyStages.Plan(1, 2, 0));
        ((IParOperationObserver)NullObserver.Instance).Plan([]);
        var plan = VerifyStages.Plan(0, 0, 0);
        CollectionAssert.AreEqual(new[] { 1.0, 1.0, 0.0 }, plan.Select(p => p.Weight).ToArray(), "empty sets still weigh something; scanning nothing until needed");
    }

    [TestMethod]
    public void Nested_DropsInnerPlans_ForwardsEverythingElse_AndTheBlockMap()
    {
        var r = new Recorder();
        using var cts = new CancellationTokenSource();
        var outer = new OperationContext { Observer = r, Threads = 3, MemoryBudget = 12345, CancellationToken = cts.Token };
        BlockStateMap? seen = null;
        outer.BlocksCreated += m => seen = m;
        var inner = outer.Nested();
        Assert.AreEqual(3, inner.Threads);
        Assert.AreEqual(12345, inner.MemoryBudget);
        Assert.AreEqual(cts.Token, inner.CancellationToken);

        inner.Observer.Plan([new OperationStage("inner", 1, "the inner operation's own plan")]);
        inner.Observer.Stage("Loading");
        inner.Observer.Progress(0.25, "a");
        inner.Observer.Log(ParLogLevel.Warning, "careful");
        CollectionAssert.AreEqual(new[] { "stage Loading", "progress 0.25 a", "log Warning careful" }, r.Events);

        var map = inner.CreateBlockMap(4, 1);
        Assert.AreSame(map, seen);
        Assert.AreSame(map, outer.Blocks);
    }
}

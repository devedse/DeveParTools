using DeveParTools.Core.Operations;

namespace DeveParTools.Core.Tests.Operations;

/// <summary>The recovery file split shared by PAR2 and PAR3: the examples from <see cref="RecoveryFileSizing"/>.</summary>
[TestClass]
public sealed class RecoveryFileSizingTests
{
    [TestMethod]
    [DataRow(10, RecoveryFileScheme.Exponential, 0, 0L, new[] { 1, 2, 4, 3 }, DisplayName = "exponential: the last file holds what is left")]
    [DataRow(10, RecoveryFileScheme.Exponential, 2, 0L, new[] { 4, 6 }, DisplayName = "exponential with 2 files: low = 4")]
    [DataRow(10, RecoveryFileScheme.Uniform, 3, 0L, new[] { 4, 3, 3 }, DisplayName = "uniform with 3 files")]
    [DataRow(10, RecoveryFileScheme.Uniform, 0, 0L, new[] { 10 }, DisplayName = "uniform without a count: one file")]
    [DataRow(2, RecoveryFileScheme.Uniform, 5, 0L, new[] { 1, 1 }, DisplayName = "uniform: never an empty file")]
    [DataRow(10, RecoveryFileScheme.LimitSize, 7, 3L, new[] { 1, 2, 3, 3, 1 }, DisplayName = "limit 3: the count is not used")]
    [DataRow(3, RecoveryFileScheme.LimitSize, 0, 0L, new[] { 1, 1, 1 }, DisplayName = "limit below 1 counts as 1")]
    [DataRow(3, RecoveryFileScheme.LimitSize, 0, long.MaxValue, new[] { 1, 2 }, DisplayName = "a huge limit is no limit")]
    [DataRow(0, RecoveryFileScheme.Exponential, 3, 0L, new int[0], DisplayName = "no recovery blocks: no files")]
    [DataRow(-1, RecoveryFileScheme.Uniform, 3, 0L, new int[0], DisplayName = "a negative count: no files")]
    public void Counts(int recovery, RecoveryFileScheme scheme, int files, long limit, int[] expected)
        => CollectionAssert.AreEqual(expected, RecoveryFileSizing.Counts(recovery, scheme, files == 0 ? null : files, limit));

    [TestMethod]
    public void ZeroFiles_MeansAutomatic() => CollectionAssert.AreEqual(new[] { 1, 2, 4, 3 }, RecoveryFileSizing.Counts(10, RecoveryFileScheme.Exponential, 0, 0));
}

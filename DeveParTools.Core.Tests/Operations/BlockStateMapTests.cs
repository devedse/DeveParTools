using DeveParTools.Core.Operations;

namespace DeveParTools.Core.Tests.Operations;

[TestClass]
public sealed class BlockStateMapTests
{
    [TestMethod]
    public void NewMap_IsAllUnknown()
    {
        var map = new BlockStateMap(100, 7);
        Assert.AreEqual(100, map.InputBlockCount);
        Assert.AreEqual(7, map.RecoveryBlockCount);
        Assert.AreEqual(0, map.Version);
        Assert.AreEqual(BlockState.Unknown, map.Get(99));
        Assert.AreEqual(BlockFlags.None, map.GetFlags(0));
        Assert.AreEqual(RecoveryBlockState.Unknown, map.GetRecovery(6));
        var counts = map.CountStates();
        Assert.AreEqual(Enum.GetValues<BlockState>().Length, counts.Length);
        Assert.AreEqual(100, counts[(int)BlockState.Unknown]);
        Assert.AreEqual(7, map.CountRecoveryStates()[(int)RecoveryBlockState.Unknown]);
        for (int g = 0; g < 4; g++)
        {
            Assert.IsFalse(map.TakeDirtyGroup(g));
        }
    }

    [TestMethod]
    public void Set_ChangesStateVersionAndDirtyGroup()
    {
        var map = new BlockStateMap(200, 2);
        map.Set(130, BlockState.Found);
        Assert.AreEqual(BlockState.Found, map.Get(130));
        Assert.AreEqual(1, map.Version);
        Assert.IsTrue(map.TakeDirtyGroup(130 / 64));
        Assert.IsFalse(map.TakeDirtyGroup(130 / 64), "taking clears the flag");
        Assert.IsFalse(map.TakeDirtyGroup(0));

        map.SetRecovery(1, RecoveryBlockState.Used);
        Assert.AreEqual(RecoveryBlockState.Used, map.GetRecovery(1));
        Assert.AreEqual(2, map.Version);
        var rec = new byte[2];
        map.CopyRecoveryStates(rec);
        CollectionAssert.AreEqual(new byte[] { 0, (byte)RecoveryBlockState.Used }, rec);
        Assert.AreEqual(1, map.CountRecoveryStates()[(int)RecoveryBlockState.Used]);
    }

    [TestMethod]
    public void SetRange_MarksEveryTouchedGroup()
    {
        var map = new BlockStateMap(1000, 0);
        map.SetRange(60, 200, BlockState.Missing); // blocks 60..259: groups 0..4
        var states = new byte[1000];
        map.CopyStates(states);
        for (int i = 0; i < 1000; i++)
        {
            Assert.AreEqual(i is >= 60 and < 260 ? (byte)BlockState.Missing : (byte)0, states[i], $"block {i}");
        }

        Assert.AreEqual(200, map.CountStates()[(int)BlockState.Missing]);
        for (int g = 0; g < 16; g++)
        {
            Assert.AreEqual(g <= 4, map.TakeDirtyGroup(g), $"group {g}");
        }

        long version = map.Version;
        map.SetRange(10, 0, BlockState.Found);
        Assert.AreEqual(version, map.Version, "an empty range changes nothing");
        Assert.IsFalse(map.TakeDirtyGroup(0));
    }

    [TestMethod]
    public void Flags_Accumulate()
    {
        var map = new BlockStateMap(3, 0);
        map.AddFlags(2, BlockFlags.Padded);
        map.AddFlags(2, BlockFlags.Deduplicated);
        Assert.AreEqual(BlockFlags.Padded | BlockFlags.Deduplicated, map.GetFlags(2));
        Assert.AreEqual(BlockFlags.None, map.GetFlags(1));
        Assert.IsTrue(map.TakeDirtyGroup(0));
    }

    [TestMethod]
    public void DirtyGroups_BeyondTheMap_AreNeverDirty()
    {
        // 64 groups share one dirty word; far groups are outside the bitmap altogether.
        var map = new BlockStateMap(64 * 64 + 1, 0);
        map.Set(64 * 64, BlockState.Damaged); // group 64: the second dirty word
        Assert.IsTrue(map.TakeDirtyGroup(64));
        Assert.IsFalse(map.TakeDirtyGroup(1 << 20));
        Assert.IsFalse(new BlockStateMap(0, 0).TakeDirtyGroup(64 * 64 * 10));
    }

    [TestMethod]
    public void Dictionaries_AreAvailableForTheFormatLayer()
    {
        var map = new BlockStateMap(2, 1);
        map.Ownership[0] = [new BlockLocation("a.bin", 0, 100)];
        map.FoundAt[1] = new BlockLocation("b.bin", 5, 100);
        map.RecoveryContainer[0] = "set.vol0+1.par2";
        Assert.AreEqual("a.bin", map.Ownership[0][0].File);
        Assert.AreEqual(5, map.FoundAt[1].Offset);
        Assert.AreEqual("set.vol0+1.par2", map.RecoveryContainer[0]);
    }

    [TestMethod]
    public void ConcurrentWriters_AreAllCounted()
    {
        var map = new BlockStateMap(10_000, 0);
        Parallel.For(0, 10_000, i => map.Set(i, (BlockState)(1 + i % 11)));
        Assert.AreEqual(10_000, map.Version);
        var counts = map.CountStates();
        Assert.AreEqual(0, counts[0]);
        Assert.AreEqual(10_000, counts.Sum());
        for (int g = 0; g < (10_000 + 63) / 64; g++)
        {
            Assert.IsTrue(map.TakeDirtyGroup(g), $"group {g}");
        }
    }
}

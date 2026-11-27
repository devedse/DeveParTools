using System.Security.Cryptography;
using DeveParTools.Core.Operations;
using DeveParTools.Par1.Tests.TestSupport;

namespace DeveParTools.Par1.Tests;

[TestClass]
public sealed class Par1VerifyRepairTests
{
    /// <summary>Writes files "f00".."fNN" (sizes vary, the last one is the largest) and creates "set.par" with volumes.</summary>
    private static Dictionary<string, byte[]> MakeSet(TempDir dir, int files, int volumes, int seed = 1)
        => MakeSet(dir, Enumerable.Range(0, files).Select(i => 100 + 37 * i).ToArray(), volumes, seed);

    private static Dictionary<string, byte[]> MakeSet(TempDir dir, int[] sizes, int volumes, int seed = 1)
    {
        var rnd = new Random(seed);
        var result = new Dictionary<string, byte[]>();
        for (int i = 0; i < sizes.Length; i++)
        {
            var data = new byte[sizes[i]];
            rnd.NextBytes(data);
            result[dir.Write($"f{i:D2}", data)] = data;
        }

        Par1Set.Create(new Par1CreateOptions { InputFiles = result.Keys.ToList(), OutputPath = dir["set.par"], RecoveryVolumes = volumes });
        return result;
    }

    private static void AssertAllRestored(Dictionary<string, byte[]> files)
    {
        foreach (var (path, data) in files)
        {
            CollectionAssert.AreEqual(data, File.ReadAllBytes(path), path);
        }
    }

    /// <summary>Rewrites a PAR1 file with a changed file list (data copied as is); used to craft unusual sets.</summary>
    private static void RewriteFileList(string path, Func<IReadOnlyList<Par1FileEntry>, IReadOnlyList<Par1FileEntry>> change)
    {
        var f = Par1File.Read(path)!;
        var data = File.ReadAllBytes(path).AsSpan((int)f.DataOffset, (int)f.DataSize).ToArray();
        Par1File.Write(path, f.VolumeNumber, change(f.Files), s => s.Write(data), data.Length);
    }

    [TestMethod]
    public void Verify_IntactSet_IsComplete_AndRepairIsANoOp()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, 3, 2);
        var ctx = new OperationContext();
        var verify = Par1Set.Verify(dir["set.par"], context: ctx);
        Assert.IsTrue(verify.AllFilesComplete);
        Assert.IsTrue(verify.RepairPossible);
        Assert.AreEqual(0, verify.MissingSavedFiles);
        Assert.AreEqual(2, verify.Volumes.Count);
        Assert.AreEqual(3, ctx.Blocks!.CountStates()[(int)BlockState.Found]);
        Assert.AreEqual("set.p01", ctx.Blocks.RecoveryContainer[0]);
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);
    }

    [TestMethod]
    public void Verify_NotAPar1File_Throws()
    {
        using var dir = new TempDir();
        dir.Write("bogus.par", new byte[300]);
        Assert.ThrowsExactly<InvalidDataException>(() => Par1Set.Verify(dir["bogus.par"]));
    }

    [TestMethod]
    public void Repair_MissingAndDamagedFiles_UpToTheVolumeCount()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, 6, 3);
        File.Delete(dir["f01"]);
        var f03 = dir.Read("f03");
        f03[7] ^= 1;
        dir.Write("f03", f03);
        File.WriteAllBytes(dir["f05"], dir.Read("f05").AsSpan(0, 50).ToArray());

        var ctx = new OperationContext();
        var verify = Par1Set.Verify(dir["set.par"], context: ctx);
        Assert.AreEqual(Par1FileStatus.Missing, verify.Files[1].Status);
        Assert.AreEqual(Par1FileStatus.Damaged, verify.Files[3].Status);
        Assert.AreEqual(Par1FileStatus.Damaged, verify.Files[5].Status, "truncated");
        Assert.AreEqual(3, verify.MissingSavedFiles);
        Assert.IsTrue(verify.RepairPossible);
        Assert.AreEqual(3, ctx.Blocks!.CountStates()[(int)BlockState.Recoverable]);

        Assert.IsTrue(Par1Set.Repair(verify, ctx));
        AssertAllRestored(files);
        Assert.IsTrue(verify.AllFilesComplete, "statuses are updated");
        Assert.IsTrue(Par1Set.Verify(dir["set.par"]).AllFilesComplete);
    }

    [TestMethod]
    public void Repair_UsesTheVolumesThatAreLeft()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, 5, 4);
        File.Delete(dir["set.p01"]);
        File.Delete(dir["set.p03"]);
        File.Delete(dir["f00"]);
        File.Delete(dir["f04"]);
        var verify = Par1Set.Verify(dir["set.par"]);
        CollectionAssert.AreEquivalent(new ulong[] { 2, 4 }, verify.Volumes.Select(v => v.VolumeNumber).ToArray());
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);
    }

    [TestMethod]
    public void TooMuchDamage_IsNotRepairable()
    {
        using var dir = new TempDir();
        MakeSet(dir, 4, 1);
        File.Delete(dir["f00"]);
        File.Delete(dir["f02"]);
        var ctx = new OperationContext();
        var verify = Par1Set.Verify(dir["set.par"], context: ctx);
        Assert.IsFalse(verify.RepairPossible);
        Assert.AreEqual(2, ctx.Blocks!.CountStates()[(int)BlockState.Unrecoverable]);
        Assert.IsFalse(Par1Set.Repair(verify));
        Assert.IsFalse(File.Exists(dir["f00"]));
    }

    [TestMethod]
    public void RenamedCopies_AreFoundInTheDirectoryAndInExtraFiles_AndCopiedBack()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, [100, 20000, 300, 400], 1);
        File.Move(dir["f00"], dir["renamed-here.bin"]);
        Directory.CreateDirectory(dir["elsewhere"]);
        File.Move(dir["f02"], dir["elsewhere/renamed.bin"]);

        // Same size and same MD5 of the first 16 KiB, but different contents: must not be taken for f01.
        var fake = dir.Read("f01");
        fake[^1] ^= 1;
        dir.Write("decoy.bin", fake);
        File.Delete(dir["f01"]);

        var ctx = new OperationContext();
        var verify = Par1Set.Verify(dir["set.par"], [dir["elsewhere/renamed.bin"], dir["does-not-exist.bin"]], ctx);
        Assert.AreEqual(dir["renamed-here.bin"], verify.Files[0].FoundAs);
        Assert.IsNull(verify.Files[1].FoundAs);
        Assert.AreEqual(dir["elsewhere/renamed.bin"], verify.Files[2].FoundAs);
        Assert.AreEqual(1, verify.MissingSavedFiles, "only f01 needs the volume");
        Assert.AreEqual(2, ctx.Blocks!.CountStates()[(int)BlockState.FoundElsewhere]);

        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);
        Assert.IsTrue(File.Exists(dir["renamed-here.bin"]), "the renamed copy is copied, not moved");
    }

    [TestMethod]
    public void Repair_OnlyRenamedCopies_NeedsNoVolumes()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, 3, 0);
        File.Move(dir["f01"], dir["moved.bin"]);
        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(0, verify.Volumes.Count);
        Assert.IsTrue(verify.RepairPossible);
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);
    }

    [TestMethod]
    public void Verify_IgnoresFilesThatAreNotUsableVolumes()
    {
        using var dir = new TempDir();
        MakeSet(dir, 3, 2);

        // Another set's volume under this set's name: different set hash.
        using (var other = new TempDir())
        {
            MakeSet(other, 2, 1, seed: 9);
            File.Copy(other["set.p01"], dir["set.p05"]);
        }

        File.Copy(dir["set.p01"], dir["set.p07"]);              // duplicate volume number 1
        var damaged = dir.Read("set.p02");
        damaged[^1] ^= 1;
        dir.Write("set.p08", damaged);                          // control hash fails
        dir.Write("set.p09", new byte[50]);                     // not a PAR1 file
        File.Copy(dir["set.par"], dir["set.p10"]);              // volume number 0
        File.Copy(dir["set.p02"], dir["set.pxy"]);              // not a .pNN extension
        File.Copy(dir["set.p02"], dir["set.p123"]);             // not a .pNN extension
        File.Copy(dir["set.p02"], dir["set.a01"]);              // below 'p'

        var verify = Par1Set.Verify(dir["set.par"], extraFiles: []);
        CollectionAssert.AreEqual(new ulong[] { 1, 2 }, verify.Volumes.Select(v => v.VolumeNumber).ToArray(), "sorted by number");
        Assert.IsTrue(verify.AllFilesComplete);
    }

    [TestMethod]
    public void Verify_AcceptsQ00StyleExtensions()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, 3, 1);
        File.Move(dir["set.p01"], dir["set.q00"]);
        File.Delete(dir["f02"]);
        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(1, verify.Volumes.Count);
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);
    }

    [TestMethod]
    public void Repair_SingularVolumeSelection_SlidesToOtherVolumes_OrFails()
    {
        // See Par1Math: with files 7 and 24 missing and only volumes 1 and 4 left, the 2×2 matrix is singular.
        using var dir = new TempDir();
        var files = MakeSet(dir, 24, 5);
        File.Move(dir["set.p02"], dir["p02.bak"]);
        File.Move(dir["set.p05"], dir["p05.bak"]);
        File.Delete(dir["set.p03"]);
        File.Delete(dir["f06"]);
        File.Delete(dir["f23"]);
        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.IsTrue(verify.RepairPossible, "the counts match");
        Assert.IsNull(Par1Set.ChooseVolumes(verify.Volumes, [6, 23], default));
        Assert.IsFalse(Par1Set.Repair(verify));
        Assert.IsFalse(File.Exists(dir["f06"]) || File.Exists(dir["f23"]), "no wrong data is written");

        // With volume 5 as well, the window slides from {1, 4} to {4, 5}.
        File.Move(dir["p05.bak"], dir["set.p05"]);
        verify = Par1Set.Verify(dir["set.par"]);
        CollectionAssert.AreEqual(new ulong[] { 4, 5 }, Par1Set.ChooseVolumes(verify.Volumes, [6, 23], default)!.Value.Volumes.Select(v => v.VolumeNumber).ToArray());
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);

        // With volume 2 instead, volumes 1 and 2 are chosen (consecutive: always invertible).
        File.Delete(dir["set.p05"]);
        File.Delete(dir["f06"]);
        File.Delete(dir["f23"]);
        File.Move(dir["p02.bak"], dir["set.p02"]);
        verify = Par1Set.Verify(dir["set.par"]);
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);
    }

    [TestMethod]
    public void Repair_TamperedVolumeWithValidControlHash_FailsTheMd5Check()
    {
        using var dir = new TempDir();
        MakeSet(dir, 3, 1);
        var vol = Par1File.Read(dir["set.p01"])!;
        var bytes = dir.Read("set.p01");
        bytes[(int)vol.DataOffset + 3] ^= 0xFF;
        MD5.HashData(bytes.AsSpan(0x20)).CopyTo(bytes, 0x10);
        dir.Write("set.p01", bytes);
        File.Delete(dir["f01"]);

        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(1, verify.Volumes.Count, "the tampered volume looks valid");
        Assert.IsFalse(Par1Set.Repair(verify));
        Assert.IsFalse(File.Exists(dir["f01"]), "wrong data is not written");
        Assert.AreEqual(Par1FileStatus.Missing, verify.Files[1].Status);
    }

    [TestMethod]
    public void NonSavedFiles_AreVerified_ButNotPartOfTheParity()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, 3, 1);
        var notSaved = dir.WriteRandom("readme.txt", 123, 5);
        var entry = new Par1FileEntry(0, 123, MD5.HashData(File.ReadAllBytes(notSaved)), MD5.HashData(File.ReadAllBytes(notSaved)), "readme.txt");
        foreach (var p in new[] { dir["set.par"], dir["set.p01"] })
        {
            // The set hash covers saved files only, so index and volume still belong together.
            RewriteFileList(p, list => [list[0], entry, list[1], list[2]]);
        }

        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(4, verify.Files.Count);
        Assert.AreEqual(1, verify.Volumes.Count);
        Assert.IsTrue(verify.AllFilesComplete);

        // A missing saved file is rebuilt; the non-saved entry sits between saved ones but has no base of its own.
        File.Delete(dir["f02"]);
        verify = Par1Set.Verify(dir["set.par"]);
        Assert.IsTrue(verify.RepairPossible);
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);

        // A missing non-saved file cannot be rebuilt from the parity.
        File.Delete(notSaved);
        verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(0, verify.MissingSavedFiles);
        Assert.IsFalse(verify.RepairPossible);
        Assert.IsFalse(Par1Set.Repair(verify));

        // ... but a renamed copy of it can be restored.
        var original = new byte[123];
        new Random(5).NextBytes(original);
        dir.Write("readme-copy.txt", original);
        verify = Par1Set.Verify(dir["set.par"]);
        Assert.IsTrue(verify.RepairPossible);
        Assert.IsTrue(Par1Set.Repair(verify));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(notSaved));
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("")]
    [DataRow("dir/..")]
    public void UnusableStoredNames_AreNeverWritten(string storedName)
    {
        using var dir = new TempDir();
        MakeSet(dir, 2, 2);
        var data = dir.Read("f00");
        foreach (var p in new[] { dir["set.par"], dir["set.p01"], dir["set.p02"] })
        {
            RewriteFileList(p, list => [list[0] with { Name = storedName }, list[1]]);
        }

        File.Delete(dir["f00"]);
        dir.Write("copy-of-f00", data);
        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(string.Empty, verify.Files[0].TargetPath);
        Assert.AreEqual(Par1FileStatus.Missing, verify.Files[0].Status);
        Assert.IsNull(verify.Files[0].FoundAs, "a copy cannot be restored without a target");
        Assert.IsTrue(verify.RepairPossible);

        var parent = Path.GetDirectoryName(dir.Path)!;
        var before = Directory.GetFileSystemEntries(parent).Concat(Directory.GetFileSystemEntries(dir.Path)).Order(StringComparer.Ordinal).ToArray();
        Assert.IsFalse(Par1Set.Repair(verify));
        var after = Directory.GetFileSystemEntries(parent).Concat(Directory.GetFileSystemEntries(dir.Path)).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(before, after, "nothing is written, neither in nor next to the set directory");
    }

    [TestMethod]
    public void StoredNamesWithDirectories_UseOnlyTheLastPart()
    {
        using var dir = new TempDir();
        var files = MakeSet(dir, 2, 1);
        foreach (var p in new[] { dir["set.par"], dir["set.p01"] })
        {
            RewriteFileList(p, list => [list[0] with { Name = @"..\..\f00" }, list[1] with { Name = "/abs/f01" }]);
        }

        Assert.AreEqual(dir["f00"], Par1Set.TargetPathOf(dir.Path, @"..\..\f00"));
        File.Delete(dir["f00"]);
        var verify = Par1Set.Verify(dir["set.par"]);
        Assert.AreEqual(dir["f01"], verify.Files[1].TargetPath);
        Assert.AreEqual(Par1FileStatus.Complete, verify.Files[1].Status);
        Assert.IsTrue(Par1Set.Repair(verify));
        AssertAllRestored(files);
    }
}

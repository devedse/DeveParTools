using System.Runtime.Versioning;
using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>Conversions of permission packet values (all platforms).</summary>
[TestClass]
public sealed class MetadataValueTests
{
    [TestMethod]
    [DataRow((ushort)0x1A0, (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead), DisplayName = "0o640")]
    [DataRow((ushort)0x9ED, 0x1ED, DisplayName = "0o4755 loses set-uid")]
    [DataRow((ushort)0xDFF, 0x1FD, DisplayName = "0o6777 loses set-uid, set-gid, sticky and world-write")]
    [DataRow((ushort)0x1FF, 0x1FD, DisplayName = "0o777 is not world-writable")]
    public void SafeUnixMode_OnlyHarmlessBits(ushort mode, int expected)
        => Assert.AreEqual((UnixFileMode)expected, Par3Metadata.SafeUnixMode(mode));

    [TestMethod]
    [DataRow((ushort)0xFFFF, DisplayName = "par3cmdline: mode not stored")]
    [DataRow((ushort)0x81A4, DisplayName = "file type bits are not allowed")]
    public void SafeUnixMode_InvalidModesAreIgnored(ushort mode) => Assert.IsNull(Par3Metadata.SafeUnixMode(mode));

    [TestMethod]
    public void UnixTime_NanosecondsOrPar3cmdlineSeconds()
    {
        var t = new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc);
        Assert.AreEqual(t, Par3Metadata.UnixTimeToUtc(1_700_000_000_000_000_000), "specification: nanoseconds");
        Assert.AreEqual(t, Par3Metadata.UnixTimeToUtc(1_700_000_000), "par3cmdline: seconds");
        Assert.AreEqual(t.AddTicks(1234567), Par3Metadata.UnixTimeToUtc(1_700_000_000_123_456_789), "100 ns resolution");
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(-86400), Par3Metadata.UnixTimeToUtc(-86400), "before 1970, in seconds");
        Assert.AreEqual(DateTime.UnixEpoch.AddDays(-365), Par3Metadata.UnixTimeToUtc(-365L * 86400 * 1_000_000_000), "before 1970, in nanoseconds");
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(99_999_999_999), Par3Metadata.UnixTimeToUtc(99_999_999_999), "the largest value read as seconds");
        Assert.IsNull(Par3Metadata.UnixTimeToUtc(Par3UnixPermissionsPacket.UnsetTime));
        Assert.IsNull(Par3Metadata.UnixTimeToUtc(-99_999_999_999), "before the year 1");
    }

    [TestMethod]
    public void FileTime_Since1601()
    {
        var t = new DateTime(2020, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(t, Par3Metadata.FileTimeToUtc((ulong)t.ToFileTimeUtc()));
        Assert.AreEqual(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), Par3Metadata.FileTimeToUtc(0));
        Assert.IsNull(Par3Metadata.FileTimeToUtc(Par3FatPermissionsPacket.UnsetTime));
        Assert.IsNull(Par3Metadata.FileTimeToUtc((ulong)(DateTime.MaxValue.Ticks - 504911232000000000L) + 1));
    }

    [TestMethod]
    public void FatAttributes_OnlyTheDefinedBits()
    {
        Assert.AreEqual(FileAttributes.Normal, Par3Metadata.FatAttributes(0));
        Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Archive, Par3Metadata.FatAttributes(0x21));
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed, Par3Metadata.FatAttributes(0x2006));
        Assert.IsNull(Par3Metadata.FatAttributes(0xFFFF), "par3cmdline: attributes not stored");
        Assert.IsNull(Par3Metadata.FatAttributes(0x10), "the directory bit is not an attribute to set");
    }
}

/// <summary>Restoring UNIX permissions, timestamps and symbolic links (not on Windows: modes and symlinks need UNIX).</summary>
[TestClass]
[OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
[UnsupportedOSPlatform("windows")]
public sealed class MetadataTests
{
    private const UnixFileMode Rw_R = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private static readonly DateTime Past = new(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    [TestMethod]
    public void RepairRestoresModeAndTimesOfFilesAndDirectories()
    {
        using var dir = new TempDir();
        var a = dir.Write("sub/a.bin", TestData.Random(1, 3000));
        var b = dir.Write("b.bin", TestData.Random(2, 100));
        File.SetUnixFileMode(a, Rw_R);
        File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetLastWriteTimeUtc(a, Past);
        File.SetLastWriteTimeUtc(b, Past.AddDays(1));
        Directory.SetLastWriteTimeUtc(dir["sub"], Past.AddDays(2));
        Par3Creator.Create(new Par3CreateOptions { Inputs = [dir.Path], OutputPath = dir["set.par3"], BlockSize = 1024, RecoveryBlockCount = 4, UnixPermissions = true });

        File.Delete(a);
        File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        var verify = Par3Files.Verify(dir["set.par3"]);
        var r = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true, RestorePermissions = true });
        Assert.IsTrue(r.Success, r.Message);
        Assert.AreEqual(Rw_R, File.GetUnixFileMode(a));
        Assert.AreEqual(Past, File.GetLastWriteTimeUtc(a));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(b), "the intact file gets its mode back too");
        Assert.AreEqual(Past.AddDays(1), File.GetLastWriteTimeUtc(b));
        Assert.AreEqual(Past.AddDays(2), Directory.GetLastWriteTimeUtc(dir["sub"]));
    }

    [TestMethod]
    public void IntactSet_PermissionsAreStillRestored()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.bin", [1, 2, 3]);
        File.SetLastWriteTimeUtc(a, Past);
        Par3Creator.Create(new Par3CreateOptions { Inputs = [a], OutputPath = dir["set.par3"], RecoveryBlockCount = 0, UnixPermissions = true });
        File.SetLastWriteTimeUtc(a, Past.AddYears(1));
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.IsTrue(verify.AllFilesComplete);
        Assert.IsTrue(Par3Repairer.Repair(verify, new Par3RepairOptions { RestorePermissions = true }).Success);
        Assert.AreEqual(Past, File.GetLastWriteTimeUtc(a));
    }

    [TestMethod]
    public void ApplyTo_PacketValues()
    {
        using var dir = new TempDir();
        var f = dir.Write("f", [1]);
        var observer = new RecordingObserver();
        var set = new InputSetId(1);

        // Set-uid and world-write are dropped; a time in the future is clamped to now.
        var future = DateTime.UtcNow.AddYears(5);
        Par3Metadata.ApplyTo(f, [Unix(set, 0xDFF, future)], isDirectory: false, observer);
        Assert.AreEqual((UnixFileMode)0x1FD, File.GetUnixFileMode(f));
        Assert.IsTrue(File.GetLastWriteTimeUtc(f) <= DateTime.UtcNow);

        // par3cmdline's "not stored" values change nothing; the FAT time is used when the UNIX time is unset.
        File.SetUnixFileMode(f, Rw_R);
        Par3Metadata.ApplyTo(f, [Unix(set, 0xFFFF, null), Par3Fat(set, Past)], isDirectory: false, observer);
        Assert.AreEqual(Rw_R, File.GetUnixFileMode(f));
        Assert.AreEqual(Past, File.GetLastWriteTimeUtc(f));

        // A par3cmdline mtime is in seconds.
        Par3Metadata.ApplyTo(f, [Unix(set, 0x180, null) with { Mtime = 1_500_000_000 }], isDirectory: false, observer);
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(1_500_000_000), File.GetLastWriteTimeUtc(f));

        // Unset everything: nothing happens.
        Par3Metadata.ApplyTo(f, [Par3Fat(set, null)], isDirectory: false, observer);
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(1_500_000_000), File.GetLastWriteTimeUtc(f));
        Assert.AreEqual(0, observer.Logs.Count);

        // Errors are warnings, not exceptions.
        Par3Metadata.ApplyTo(dir["missing"], [Unix(set, 0x180, Past)], isDirectory: false, observer);
        StringAssert.StartsWith(observer.Warnings.Single(), "Could not apply metadata to");
    }

    [TestMethod]
    public void FatAttributes_AreSetOnWindows_AndIgnoredElsewhere()
    {
        using var dir = new TempDir();
        var f = dir.Write("fat", [1]);
        var observer = new RecordingObserver();

        // 0x21 = read-only + archive.
        Par3Metadata.ApplyTo(f, [Par3Fat(new InputSetId(2), null) with { Attributes = 0x21 }], isDirectory: false, observer);
        Assert.AreEqual(OperatingSystem.IsWindows(), File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly));
        Assert.AreEqual(0, observer.Logs.Count);
        File.SetAttributes(f, FileAttributes.Normal);
    }

    private static Par3UnixPermissionsPacket Unix(InputSetId set, ushort mode, DateTime? mtime)
        => new(-1, -1, mtime == null ? Par3UnixPermissionsPacket.UnsetTime : (mtime.Value - DateTime.UnixEpoch).Ticks * 100, uint.MaxValue, uint.MaxValue, mode, string.Empty, string.Empty, []) { SetId = set };

    private static Par3FatPermissionsPacket Par3Fat(InputSetId set, DateTime? write)
        => new(Par3FatPermissionsPacket.UnsetTime, Par3FatPermissionsPacket.UnsetTime, write == null ? Par3FatPermissionsPacket.UnsetTime : (ulong)write.Value.ToFileTimeUtc(), 0xFFFF) { SetId = set };

    [TestMethod]
    public void Links_OnlyInsideTheBaseDirectory()
    {
        using var dir = new TempDir();
        var b = new Par3SetBuilder(64, [0x1D]);
        var target = TestData.Random(1, 100);
        var file = b.File("target.bin", target);
        Fingerprint Link(string name, string[] path, bool absolute = false, bool symbolic = true) => b.Add(Par3PacketWriter.Link(b.SetId, symbolic, name, path, absolute, []));
        var good = Link("good", ["target.bin"]);
        var hard = Link("hard", ["sub", "inner.bin"], symbolic: false);
        var nested = Link("up", ["target.bin"]);
        var absolute = Link("abs", ["etc", "passwd"], absolute: true);
        var dotdot = Link("escape", ["..", "x"]);
        var dot = Link("dot", [".", "target.bin"]);
        var empty = Link("empty", ["", "target.bin"]);
        var badName = Link("..", ["target.bin"]);
        var existing = Link("existing", ["target.bin"]);
        var blocked = Link("blocked", ["target.bin"]);
        var sub = b.Directory("sub", [b.File("inner.bin", [5, 6, 7])], [nested]);
        var parentIsFile = b.Directory("file-not-dir", [], [blocked]);
        b.Root([file, sub, parentIsFile], [good, hard, absolute, dotdot, dot, empty, badName, existing]);
        b.WriteAll(dir["set.par3"]);
        dir.Write("target.bin", target);
        dir.Write("sub/inner.bin", [5, 6, 7]);
        dir.Write("existing", [42]);
        dir.Write("file-not-dir", [0]); // the directory of "blocked" is a file

        var observer = new RecordingObserver();
        var verify = Par3Files.Verify(dir["set.par3"]);
        Assert.AreEqual(10, verify.Set.Links.Count);
        Par3Metadata.CreateLinks(verify, observer);

        Assert.AreEqual("target.bin", new FileInfo(dir["good"]).LinkTarget);
        CollectionAssert.AreEqual(target, dir.Read("good"));
        Assert.AreEqual(Path.Combine("sub", "inner.bin"), new FileInfo(dir["hard"]).LinkTarget, "hard links are created as symbolic links");
        Assert.AreEqual(Path.Combine("..", "target.bin"), new FileInfo(dir["sub/up"]).LinkTarget, "relative to the link's directory");
        CollectionAssert.AreEqual(target, dir.Read("sub/up"));
        CollectionAssert.AreEqual(new byte[] { 42 }, dir.Read("existing"), "existing files are not replaced");
        foreach (var n in new[] { "abs", "escape", "dot", "empty" })
        {
            Assert.IsFalse(File.Exists(dir[n]) || new FileInfo(dir[n]).LinkTarget != null, n);
        }

        Assert.AreEqual(4, observer.Warnings.Count(w => w.Contains("leaves the base directory", StringComparison.Ordinal)));
        Assert.AreEqual(1, observer.Warnings.Count(w => w.StartsWith("Skipping unsafe link", StringComparison.Ordinal)));
        Assert.AreEqual(1, observer.Warnings.Count(w => w.StartsWith("Could not create link 'file-not-dir/blocked'", StringComparison.Ordinal)));

        // Through the repairer, which also creates the missing directory.
        File.Delete(dir["good"]);
        File.Delete(dir["file-not-dir"]);
        Assert.IsTrue(Par3Repairer.Repair(Par3Files.Verify(dir["set.par3"]), new Par3RepairOptions { RestoreLinks = true }).Success);
        Assert.AreEqual("target.bin", new FileInfo(dir["good"]).LinkTarget);
        Assert.AreEqual(Path.Combine("..", "target.bin"), new FileInfo(dir["file-not-dir/blocked"]).LinkTarget);
    }
}

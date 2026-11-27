using DeveParTools.Core.Util;

namespace DeveParTools.Core.Tests.Util;

[TestClass]
public sealed class PathSafetyTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "pathsafety-base");

    [TestMethod]
    [DataRow("file.bin", "file.bin")]
    [DataRow("dir/sub/file.bin", "dir/sub/file.bin")]
    [DataRow("dir\\sub\\file.bin", "dir/sub/file.bin")] // Windows separators in a stored name are accepted
    [DataRow("..notes", "..notes")] // a name that merely starts with dots is fine
    public void SafeNames_ResolveBelowTheBase(string stored, string expectedRelative)
    {
        Assert.IsTrue(PathSafety.TryResolveUnder(Base, stored, out var path, out var reason));
        Assert.AreEqual(string.Empty, reason);
        Assert.AreEqual(Path.GetFullPath(Path.Combine([Base, .. expectedRelative.Split('/')])), path);
        Assert.AreEqual(path, PathSafety.ResolveUnder(Base, stored));
        Assert.AreEqual(expectedRelative, PathSafety.ToStoredName(Base, path), "round trip");
    }

    [TestMethod]
    [DataRow("", "empty name")]
    [DataRow("/etc/passwd", "absolute path")]
    [DataRow("\\windows\\system32", "absolute path")]
    [DataRow("C:evil.bin", "absolute path")]
    [DataRow("c:/evil.bin", "absolute path")]
    [DataRow("../evil.bin", "invalid path segment")]
    [DataRow("dir/../../evil.bin", "invalid path segment")]
    [DataRow("dir/./file.bin", "invalid path segment")]
    [DataRow("dir//file.bin", "invalid path segment")]
    [DataRow("dir/", "invalid path segment")]
    [DataRow("..", "invalid path segment")]
    [DataRow("dir/fi\0le.bin", "NUL character")]
    public void UnsafeNames_AreRejected(string stored, string expectedReason)
    {
        Assert.IsFalse(PathSafety.TryResolveUnder(Base, stored, out var path, out var reason));
        Assert.AreEqual(expectedReason, reason);
        Assert.AreEqual(string.Empty, path);
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => PathSafety.ResolveUnder(Base, stored));
        StringAssert.Contains(ex.Message, expectedReason);
    }

    [TestMethod]
    public void NullName_IsRejectedAsEmpty()
    {
        Assert.IsFalse(PathSafety.TryResolveUnder(Base, null!, out _, out var reason));
        Assert.AreEqual("empty name", reason);
    }

    [TestMethod]
    public void BaseWithTrailingSeparator_Works()
    {
        var withSeparator = Base + Path.DirectorySeparatorChar;
        Assert.AreEqual(Path.Combine(Base, "x"), PathSafety.ResolveUnder(withSeparator, "x"));
        Assert.AreEqual("d/x", PathSafety.ToStoredName(withSeparator, Path.Combine(Base, "d", "x")));
    }

    [TestMethod]
    public void SiblingDirectoryWithTheSamePrefix_IsNotBelowTheBase()
    {
        // "/tmp/pathsafety-base-2/x" starts with the base path as a string, but is not inside it.
        Assert.ThrowsExactly<ArgumentException>(() => PathSafety.ToStoredName(Base, Base + "-2" + Path.DirectorySeparatorChar + "x"));
        Assert.ThrowsExactly<ArgumentException>(() => PathSafety.ToStoredName(Base, Path.Combine(Path.GetDirectoryName(Base)!, "x")));
        Assert.ThrowsExactly<ArgumentException>(() => PathSafety.ToStoredName(Base, Path.GetDirectoryName(Base)!));
    }

    /// <summary>On Windows a later segment can carry a drive ("a/C:x"), which Path.Combine treats as a new root.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void DriveInALaterSegment_EscapesTheBase_OnWindows()
    {
        Assert.IsFalse(PathSafety.TryResolveUnder(Base, "a/C:evil.bin", out _, out var reason));
        Assert.AreEqual("escapes the base directory", reason);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void OtherDrive_IsNotBelowTheBase_OnWindows()
    {
        string other = char.ToUpperInvariant(Path.GetFullPath(Base)[0]) == 'Z' ? @"Y:\x" : @"Z:\x";
        Assert.ThrowsExactly<ArgumentException>(() => PathSafety.ToStoredName(Base, other));
    }
}

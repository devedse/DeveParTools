using DeveParTools.Core.IO;

namespace DeveParTools.Core.Tests.IO;

[TestClass]
public sealed class FileHandleCacheTests
{
    private string _dir = "";

    [TestInitialize]
    public void CreateDirectory() => _dir = TestData.CreateTempDirectory();

    [TestCleanup]
    public void DeleteDirectory() => TestData.DeleteDirectory(_dir);

    [TestMethod]
    public void Get_CachesOneHandlePerPath()
    {
        var a = WriteFile("a.bin", [1, 2, 3]);
        var b = WriteFile("b.bin", [4]);
        using var cache = new FileHandleCache();
        Assert.AreSame(cache.Get(a), cache.Get(a));
        Assert.AreNotSame(cache.Get(a), cache.Get(b));
    }

    [TestMethod]
    public void ReadPadded_ZeroFillsPastTheEnd()
    {
        var path = WriteFile("data.bin", [1, 2, 3, 4, 5]);
        using var cache = new FileHandleCache();
        var buf = Enumerable.Repeat((byte)0xEE, 8).ToArray();
        Assert.AreEqual(3, cache.ReadPadded(path, 2, buf));
        CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 0, 0, 0, 0, 0 }, buf);
        Assert.AreEqual(0, cache.ReadPadded(path, 100, buf), "entirely past the end");
        CollectionAssert.AreEqual(new byte[8], buf);
        Assert.AreEqual(2, FileHandleCache.ReadPadded(cache.Get(path), 0, buf.AsSpan(0, 2)));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, buf[..2]);
    }

    [TestMethod]
    public void ReadPadded_LargeReadsAreComplete()
    {
        var data = TestData.RandomBytes(new Random(1), 3 << 20);
        var path = WriteFile("big.bin", data);
        using var cache = new FileHandleCache();
        var buf = new byte[data.Length + 10];
        Assert.AreEqual(data.Length, cache.ReadPadded(path, 0, buf));
        CollectionAssert.AreEqual(data, buf[..data.Length]);
    }

    [TestMethod]
    public void Close_ReleasesTheHandle_SoTheFileCanBeReplaced()
    {
        var path = WriteFile("c.bin", [1]);
        using var cache = new FileHandleCache();
        var first = cache.Get(path);
        cache.Close(path);
        Assert.IsTrue(first.IsClosed);
        cache.Close(path); // not cached any more: no-op
        File.WriteAllBytes(path, [9, 9]);
        var buf = new byte[2];
        Assert.AreEqual(2, cache.ReadPadded(path, 0, buf));
        CollectionAssert.AreEqual(new byte[] { 9, 9 }, buf);
        Assert.AreNotSame(first, cache.Get(path));
    }

    [TestMethod]
    public void Dispose_ClosesEverything()
    {
        var a = WriteFile("a.bin", [1]);
        var b = WriteFile("b.bin", [2]);
        var cache = new FileHandleCache();
        var ha = cache.Get(a);
        var hb = cache.Get(b);
        cache.Dispose();
        Assert.IsTrue(ha.IsClosed);
        Assert.IsTrue(hb.IsClosed);
        cache.Dispose();
    }

    [TestMethod]
    public void MissingFile_Throws()
    {
        using var cache = new FileHandleCache();
        Assert.ThrowsExactly<FileNotFoundException>(() => cache.Get(Path.Combine(_dir, "nope.bin")));
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}

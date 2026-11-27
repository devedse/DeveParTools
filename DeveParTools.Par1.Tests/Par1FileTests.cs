using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeveParTools.Par1.Tests.TestSupport;

namespace DeveParTools.Par1.Tests;

/// <summary>The .par / .pXX file format: header fields, file list, hashes and robustness against damaged headers.</summary>
[TestClass]
public sealed class Par1FileTests
{
    private static Par1FileEntry Entry(string name, int size, ulong status = 1)
    {
        var data = new byte[size];
        new Random(size).NextBytes(data);
        return new Par1FileEntry(status, size, MD5.HashData(data), MD5.HashData(data.AsSpan(0, Math.Min(size, 16384))), name);
    }

    private static string WriteSample(TempDir dir, string name = "s.par", ulong volume = 0, string comment = "note")
    {
        var path = dir[name];
        var c = Encoding.Unicode.GetBytes(comment);
        Par1File.Write(path, volume, [Entry("a.bin", 10), Entry("bé.bin", 20000), Entry("skipped.txt", 3, status: 0)], s => s.Write(c), c.Length);
        return path;
    }

    /// <summary>Overwrites a 64-bit header field and (optionally) recomputes the control hash, so only the field is "wrong".</summary>
    private static void PatchU64(string path, int offset, ulong value, bool fixControlHash = true)
    {
        var b = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(offset), value);
        if (fixControlHash)
        {
            MD5.HashData(b.AsSpan(0x20)).CopyTo(b, 0x10);
        }

        File.WriteAllBytes(path, b);
    }

    [TestMethod]
    public void WriteThenRead_RoundTripsEveryField()
    {
        using var dir = new TempDir();
        var path = WriteSample(dir);
        var bytes = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(Par1File.Magic, bytes.AsSpan(0, 8).ToArray());
        Assert.AreEqual(Par1File.FileVersion, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));

        var f = Par1File.Read(path)!;
        Assert.AreEqual(path, f.Path);
        Assert.AreEqual(Par1File.ProgramVersion, f.ProgramId);
        Assert.AreEqual(0UL, f.VolumeNumber);
        Assert.IsTrue(f.ControlHashValid);
        Assert.AreEqual("note", f.Comment);
        Assert.AreEqual(3, f.Files.Count);
        Assert.AreEqual("bé.bin", f.Files[1].Name);
        Assert.AreEqual(20000, f.Files[1].Size);
        Assert.IsTrue(f.Files[0].IsSaved);
        Assert.IsFalse(f.Files[2].IsSaved);
        CollectionAssert.AreEqual(Entry("a.bin", 10).Md5, f.Files[0].Md5);
        CollectionAssert.AreEqual(Entry("bé.bin", 20000).Md5Of16k, f.Files[1].Md5Of16k);
        Assert.AreEqual(f.DataOffset + f.DataSize, bytes.Length);

        // Set hash: MD5 over the MD5s of the saved files only, in list order.
        CollectionAssert.AreEqual(MD5.HashData([.. f.Files[0].Md5, .. f.Files[1].Md5]), f.SetHash);
        CollectionAssert.AreEqual(f.SetHash, Par1File.SetHashOf(f.Files));
    }

    [TestMethod]
    public void Volumes_HaveNoComment_AndADamagedFileFailsTheControlHash()
    {
        using var dir = new TempDir();
        var path = WriteSample(dir, "s.p01", volume: 1, comment: "not a comment for volumes");
        var v = Par1File.Read(path)!;
        Assert.AreEqual(1UL, v.VolumeNumber);
        Assert.IsNull(v.Comment, "only the index file (volume 0) carries a comment");

        var b = File.ReadAllBytes(path);
        b[^1] ^= 0x55;
        File.WriteAllBytes(path, b);
        Assert.IsFalse(Par1File.Read(path)!.ControlHashValid);
    }

    [TestMethod]
    public void Read_NotAPar1File_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.IsNull(Par1File.Read(dir.Write("short.par", new byte[Par1File.HeaderSize - 1])), "shorter than the header");
        Assert.IsNull(Par1File.Read(dir.Write("zeros.par", new byte[200])), "no magic");

        var path = WriteSample(dir);
        var b = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0x00020000);
        Assert.IsNull(Par1File.Read(dir.Write("v2.par", b)), "unknown version");
    }

    [TestMethod]
    [DataRow(0x40, 0x10UL, DisplayName = "list offset inside the header")]
    [DataRow(0x40, 0x7FFF_FFFF_FFFF_FFF0UL, DisplayName = "list offset beyond the file")]
    [DataRow(0x48, 0x7FFF_FFFF_FFFF_FFF0UL, DisplayName = "list size overflowing offset + size")]
    [DataRow(0x48, 0x8000_0000_0000_0000UL, DisplayName = "list size of 2^63 (negative as long)")]
    [DataRow(0x38, 0x8000_0000_0000_0000UL, DisplayName = "negative file count")]
    [DataRow(0x38, 0x10001UL, DisplayName = "more than 65536 files")]
    [DataRow(0x38, 4UL, DisplayName = "more files than the list holds")]
    public void Read_DamagedHeaderFields_ReturnNull(int offset, ulong value)
    {
        using var dir = new TempDir();
        var path = WriteSample(dir);
        PatchU64(path, offset, value);
        Assert.IsNull(Par1File.Read(path));
    }

    [TestMethod]
    [DataRow(0x37UL, DisplayName = "entry shorter than its fixed part")]
    [DataRow(0x10000UL, DisplayName = "entry longer than the list")]
    public void Read_DamagedFileEntry_ReturnsNull(ulong entrySize)
    {
        using var dir = new TempDir();
        var path = WriteSample(dir);
        PatchU64(path, Par1File.HeaderSize, entrySize);
        Assert.IsNull(Par1File.Read(path));
    }

    [TestMethod]
    [DataRow(0x50, 0x8000_0000_0000_0000UL, DisplayName = "negative data offset")]
    [DataRow(0x50, 0x7FFF_FFFF_FFFF_FFF0UL, DisplayName = "data offset beyond the file")]
    [DataRow(0x58, 0x7FFF_FFFF_FFFF_FFF0UL, DisplayName = "data size overflowing offset + size")]
    [DataRow(0x58, 0x8000_0000_0000_0000UL, DisplayName = "negative data size")]
    public void Read_DamagedDataRange_SkipsTheCommentButKeepsTheFileList(int offset, ulong value)
    {
        using var dir = new TempDir();
        var path = WriteSample(dir);
        PatchU64(path, offset, value);
        var f = Par1File.Read(path)!;
        Assert.IsNull(f.Comment);
        Assert.AreEqual(3, f.Files.Count);
        Assert.IsTrue(f.ControlHashValid);
    }

    [TestMethod]
    public void Read_DamagedHeaderWithoutFixedHash_IsStillParsedButFlagged()
    {
        using var dir = new TempDir();
        var path = WriteSample(dir);
        PatchU64(path, 0x30, 7, fixControlHash: false);
        var f = Par1File.Read(path)!;
        Assert.AreEqual(7UL, f.VolumeNumber);
        Assert.IsFalse(f.ControlHashValid);
    }

    [TestMethod]
    [DataRow(0, ".par")]
    [DataRow(1, ".p01")]
    [DataRow(42, ".p42")]
    [DataRow(99, ".p99")]
    [DataRow(100, ".q00")]
    [DataRow(254, ".r54")]
    public void VolumeExtension_CountsP01ToP99ThenQ00(int volume, string expected)
        => Assert.AreEqual(expected, Par1File.VolumeExtension(volume));

    [TestMethod]
    public void Coefficients_FollowPlank()
    {
        // Volume 1 is plain XOR parity; volume v uses base^(v-1) with base = 1-based file index.
        Assert.AreEqual((byte)1, Par1Math.Coefficient(5, 1));
        Assert.AreEqual((byte)3, Par1Math.Coefficient(2, 2));
        Assert.AreEqual(Par1Math.Field.Mul(3, 3), Par1Math.Coefficient(2, 3));
        Assert.AreEqual(0x11D, Par1Math.Field.Polynomial);
    }

    [TestMethod]
    public void Coefficients_TheDocumentedSingularExample()
    {
        // Files 7 and 24 (indices 6 and 23) with volumes 1 and 4: det = 7^0·24^3 + 7^3·24^0 = 0 (see Par1Math remarks).
        var f = Par1Math.Field;
        byte det = (byte)(f.Mul(Par1Math.Coefficient(6, 1), Par1Math.Coefficient(23, 4)) ^ f.Mul(Par1Math.Coefficient(6, 4), Par1Math.Coefficient(23, 1)));
        Assert.AreEqual((byte)0, det);
    }
}

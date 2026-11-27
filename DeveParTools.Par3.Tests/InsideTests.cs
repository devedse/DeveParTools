using System.Buffers.Binary;
using System.IO.Compression;
using DeveParTools.Par3.Tests.TestSupport;

namespace DeveParTools.Par3.Tests;

/// <summary>"PAR inside": archives that carry their own recovery data.</summary>
[TestClass]
public sealed class InsideTests
{
    internal static byte[] MakeZip(int seed, int files = 4, int size = 30_000, string? comment = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < files; i++)
            {
                using var s = zip.CreateEntry($"file{i}.bin", CompressionLevel.NoCompression).Open();
                s.Write(TestData.Random(seed + i, size + i * 333));
            }

            if (comment != null)
            {
                zip.Comment = comment;
            }
        }

        return ms.ToArray();
    }

    /// <summary>A minimal 7z layout: 32-byte start header, packed data, and a "next header" that ends with kEnd (00 00).</summary>
    internal static byte[] Make7z(int seed, int dataLength = 50_000)
    {
        var data = TestData.Random(seed, dataLength);
        var nextHeader = TestData.Random(seed + 1, 30).Concat(new byte[] { 0, 0 }).ToArray();
        var file = new byte[32 + data.Length + nextHeader.Length];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(file, 0);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(12), data.Length);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(20), nextHeader.Length);
        data.CopyTo(file, 32);
        nextHeader.CopyTo(file, 32 + data.Length);
        return file;
    }

    internal static byte[] MakeRar(int seed, int dataLength = 40_000) => "Rar!\x1A\x07\x01\x00"u8.ToArray().Concat(TestData.Random(seed, dataLength)).ToArray();

    private static void Damage(string path, long offset, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var bytes = new byte[count];
        RandomAccess.Read(fs.SafeFileHandle, bytes, offset);
        for (int i = 0; i < count; i++)
        {
            bytes[i] ^= 0x77;
        }

        RandomAccess.Write(fs.SafeFileHandle, bytes, offset);
    }

    private static void VerifyRepairDelete(TempDir dir, string name, byte[] original, Par3InsideResult result)
    {
        var path = dir[name];
        Assert.AreEqual(original.Length, result.OriginalLength);
        Assert.AreEqual(result.NewLength, new FileInfo(path).Length);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path).AsSpan(0, original.Length).ToArray(), "the original bytes are untouched");
        Assert.IsTrue(Par3Inside.ContainsPar3Packets(path));

        var verify = Par3Files.Verify(path);
        var self = verify.Files.Single();
        Assert.AreEqual(Par3FileStatus.Complete, self.Status);
        Assert.AreEqual(path, Path.GetFullPath(self.TargetPath));

        // Damage protected data near the start and inside the footer copy at the very end.
        Damage(path, 100, 50);
        Damage(path, result.NewLength - 10, 5);
        verify = Par3Files.Verify(path);
        Assert.AreEqual(Par3FileStatus.Damaged, verify.Files.Single().Status);
        Assert.IsTrue(verify.RepairPossible);
        var repair = Par3Repairer.Repair(verify, new Par3RepairOptions { PurgeBackups = true });
        Assert.IsTrue(repair.Success, repair.Message);
        Assert.AreEqual(Par3FileStatus.Complete, Par3Files.Verify(path).Files.Single().Status);

        Assert.AreEqual(original.Length, Par3Inside.Delete(path));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Zip_InsertVerifyRepairDelete_AndZipReadersStillWork()
    {
        using var dir = new TempDir();
        var original = MakeZip(1, comment: "a comment that is part of the footer");
        dir.Write("archive.zip", original);
        var result = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["archive.zip"], RedundancyPercent = 5 });
        Assert.AreEqual(Par3InsideFormat.Zip, result.Format);
        Assert.IsTrue(result.RecoveryBlocks >= 1);

        // .NET's ZipArchive finds the footer copy at the end and reads every entry.
        using (var zip = ZipFile.OpenRead(dir["archive.zip"]))
        {
            Assert.AreEqual(4, zip.Entries.Count);
            using var s = zip.Entries[2].Open();
            var content = new MemoryStream();
            s.CopyTo(content);
            CollectionAssert.AreEqual(TestData.Random(3, 30_000 + 2 * 333), content.ToArray());
        }

        VerifyRepairDelete(dir, "archive.zip", original, result);
    }

    [TestMethod]
    public void SevenZip_AndRar_AreAppendedTo()
    {
        using var dir = new TempDir();
        var sevenZip = Make7z(5);
        dir.Write("a.7z", sevenZip);
        var r7 = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["a.7z"], RecoveryBlockCount = 3 });
        Assert.AreEqual(Par3InsideFormat.SevenZip, r7.Format);
        Assert.AreEqual(3, r7.RecoveryBlocks);
        VerifyRepairDelete(dir, "a.7z", sevenZip, r7);

        var rar = MakeRar(6);
        dir.Write("b.rar", rar);
        var rr = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["b.rar"], RedundancyPercent = 10, BlockSize = 1024 });
        Assert.AreEqual(Par3InsideFormat.Rar, rr.Format);
        Assert.AreEqual(1024UL, rr.BlockSize);
        VerifyRepairDelete(dir, "b.rar", rar, rr);
    }

    [TestMethod]
    [DataRow(100)]
    [DataRow(5_000)]
    [DataRow(300_000)]
    public void BlockSizeChoice_WorksForSmallAndLargeArchives(int size)
    {
        using var dir = new TempDir();
        var original = MakeZip(size, files: 1, size: size);
        dir.Write("s.zip", original);
        var result = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["s.zip"], RedundancyPercent = 50 });
        Assert.IsTrue(result.BlockSize >= 40);
        Assert.IsTrue(result.InputBlocks <= 4096, result.InputBlocks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        VerifyRepairDelete(dir, "s.zip", original, result);
    }

    [TestMethod]
    public void InitialBlockSize_FollowsPar3cmdline()
    {
        Assert.AreEqual(40UL, Par3Inside.InitialBlockSize(10));
        Assert.AreEqual(40UL, Par3Inside.InitialBlockSize(5000));
        // 10·√300000 ≈ 5477 gives only 54 blocks (< 128), so size / 256 rounded up to a power of two: 2048.
        Assert.AreEqual(2048UL, Par3Inside.InitialBlockSize(300_000));
        // 10·√2e8 ≈ 141421 → 262144 (762 blocks).
        Assert.AreEqual(262144UL, Par3Inside.InitialBlockSize(200_000_000));
        Assert.IsTrue(Par3Inside.InitialBlockSize(1UL << 40) * 2048 >= 1UL << 40);
    }

    [TestMethod]
    public void Refusals()
    {
        using var dir = new TempDir();
        dir.Write("plain.bin", TestData.Random(1, 1000));
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["plain.bin"] }));
        dir.Write("tiny.zip", [1, 2, 3]);
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["tiny.zip"] }));
        Assert.IsNull(Par3Inside.FormatOf(dir["tiny.zip"]));

        // Twice: the second insert sees the packets.
        dir.Write("twice.zip", MakeZip(2));
        Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["twice.zip"] });
        var again = Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["twice.zip"] }));
        StringAssert.Contains(again.Message, "already contains PAR3 data");

        // ZIP with junk after the footer, and with a footer that points nowhere.
        dir.Write("junk.zip", MakeZip(3).Concat(new byte[100]).ToArray());
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["junk.zip"] })).Message, "extra data");
        var broken = MakeZip(4);
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(broken.Length - 22 + 16), 1_000_000_000);
        dir.Write("broken.zip", broken);
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["broken.zip"] }));
        var noEocd = MakeZip(5);
        noEocd.AsSpan(noEocd.Length - 22, 4).Clear();
        dir.Write("noeocd.zip", noEocd);
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["noeocd.zip"] })).Message, "end-of-central-directory");

        // 7z with extra data, or with a wrong end marker.
        dir.Write("extra.7z", Make7z(6).Concat(new byte[10]).ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["extra.7z"] }));
        var badEnd = Make7z(7);
        badEnd[^1] = 1;
        dir.Write("badend.7z", badEnd);
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["badend.7z"] }));
        var shortHeader = Make7z(8);
        BinaryPrimitives.WriteInt64LittleEndian(shortHeader.AsSpan(20), 1_000_000);
        dir.Write("short.7z", shortHeader);
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["short.7z"] }));
        dir.Write("tiny.7z", Make7z(9).AsSpan(0, 20).ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["tiny.7z"] }));
    }

    [TestMethod]
    public void Delete_RefusesDamagedArchives_AndOrdinaryPar3Sets()
    {
        using var dir = new TempDir();
        dir.Write("d.zip", MakeZip(10));
        var result = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["d.zip"], RedundancyPercent = 10 });
        Damage(dir["d.zip"], 200, 10);
        Assert.ThrowsExactly<InvalidOperationException>(() => Par3Inside.Delete(dir["d.zip"]));
        Assert.AreEqual(result.NewLength, new FileInfo(dir["d.zip"]).Length, "nothing was removed");

        // A normal set protects other files: there is nothing "inside" to delete.
        dir.Write("x.bin", TestData.Random(11, 5000));
        Par3Creator.Create(new Par3CreateOptions { Inputs = [dir["x.bin"]], OutputPath = dir["x.par3"], BlockSize = 1024, RecoveryBlockCount = 1 });
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Delete(dir["x.par3"]));
    }

    /// <summary>Rewrites a ZIP's footer as ZIP64: a ZIP64 EOCD record and locator, and an EOCD with saturated fields.</summary>
    private static byte[] ToZip64(byte[] zip, bool brokenLocator = false)
    {
        int eocd = zip.Length - 22;
        uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 12));
        uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));
        ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 10));
        var record = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), cdSize);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), cdOffset);
        var locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)eocd + (brokenLocator ? 7UL : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);
        var end = zip.AsSpan(eocd, 22).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), 0xFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), uint.MaxValue);
        return [.. zip.AsSpan(0, eocd), .. record, .. locator, .. end];
    }

    [TestMethod]
    public void Zip64Footer_IsCopiedWhole()
    {
        using var dir = new TempDir();
        var original = ToZip64(MakeZip(20));
        dir.Write("z64.zip", original);
        Assert.AreEqual(98, Par3Inside.ZipFooterLength(dir["z64.zip"], original.Length), "ZIP64 record + locator + EOCD");
        var result = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["z64.zip"], RedundancyPercent = 10, BlockSize = 64 });
        CollectionAssert.AreEqual(original.AsSpan(original.Length - 98).ToArray(), File.ReadAllBytes(dir["z64.zip"]).AsSpan((int)result.NewLength - 98).ToArray(), "the footer copy ends the file");
        Assert.AreEqual(original.Length, Par3Inside.Delete(dir["z64.zip"]));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(dir["z64.zip"]));

        // A locator that points nowhere: no valid footer.
        var broken = ToZip64(MakeZip(21), brokenLocator: true);
        dir.Write("broken64.zip", broken);
        Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.ZipFooterLength(dir["broken64.zip"], broken.Length));
    }

    [TestMethod]
    public void Zip_DataBetweenCentralDirectoryAndEnd_IsRefused()
    {
        using var dir = new TempDir();
        var zip = MakeZip(22);
        var padded = zip.AsSpan(0, zip.Length - 22).ToArray().Concat(new byte[50]).Concat(zip.AsSpan(zip.Length - 22).ToArray()).ToArray();
        dir.Write("gap.zip", padded);
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["gap.zip"] })).Message, "extra data");
    }

    [TestMethod]
    public void LargeFooter_GetsFullBlocks_AndSeparateTailBlocks()
    {
        using var dir = new TempDir();

        // A 2978-byte comment makes a 3000-byte footer: two full 1024-byte blocks and a 952-byte tail.
        var original = MakeZip(23, comment: new string('c', 2978));
        dir.Write("big.zip", original);
        ulong dataTail = (ulong)(original.Length - 3000) % 1024;
        Assert.IsTrue(dataTail >= 40 && dataTail + 952 > 1024, $"the two tails need separate blocks (data tail {dataTail})");
        var result = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["big.zip"], RecoveryBlockCount = 4, BlockSize = 1024 });
        VerifyRepairDelete(dir, "big.zip", original, result);
    }

    [TestMethod]
    public void RecoveryWrittenInSlices_GetsItsChecksumFromDisk()
    {
        using var dir = new TempDir();
        var original = MakeZip(24, files: 2, size: 600_000);
        dir.Write("slices.zip", original);

        // A tiny memory budget makes the combiner write each recovery block in several slices.
        var context = new Core.Operations.OperationContext { MemoryBudget = 64 * 1024 };
        var result = Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["slices.zip"], RecoveryBlockCount = 2, BlockSize = 256 * 1024 }, context);
        VerifyRepairDelete(dir, "slices.zip", original, result);
    }

    [TestMethod]
    public void Cancellation_LeavesTheArchiveAsItWas()
    {
        using var dir = new TempDir();
        var original = MakeZip(25);
        dir.Write("c.zip", original);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Par3Inside.Insert(new Par3InsideOptions { ArchivePath = dir["c.zip"] }, new Core.Operations.OperationContext { CancellationToken = cts.Token }));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(dir["c.zip"]));
    }

    [TestMethod]
    public void Delete_FileThatProtectsItselfWithoutAnUnprotectedChunk_IsNotInsideData()
    {
        using var dir = new TempDir();
        dir.Write("a.bin", TestData.Random(26, 3000));
        Par3Creator.Create(new Par3CreateOptions { Inputs = [dir["a.bin"]], OutputPath = dir["a.par3"], BlockSize = 1024, RecoveryBlockCount = 1 });

        // Append the set to the protected file itself: its File packet now describes the file that carries it.
        File.WriteAllBytes(dir["a.bin"], [.. dir.Read("a.bin"), .. dir.Read("a.par3")]);
        File.Delete(dir["a.par3"]);
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => Par3Inside.Delete(dir["a.bin"])).Message, "no unprotected chunk");
    }
}

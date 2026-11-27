using DeveParTools.Core.Hashing;
using DeveParTools.Par2.Packets;
using DeveParTools.Par2.Tests.TestSupport;

namespace DeveParTools.Par2.Tests;

/// <summary>The CRC-32 + MD5 slice matcher used by the rolling search of the verifier.</summary>
[TestClass]
public sealed class Par2SliceMatcherTests
{
    private const int SliceSize = 64;

    /// <summary>An assembled set with correct descriptions and slice checksums for the given file contents.</summary>
    private static Par2RecoverySet SetOf(params byte[]?[] files)
    {
        var s = Md5Hash.Compute("set"u8);
        var r = new Par2FileReadResult { Path = "x.par2" };
        var ids = new List<Md5Hash>();
        for (int i = 0; i < files.Length; i++)
        {
            var id = Md5Hash.Compute([(byte)i]);
            ids.Add(id);
            var data = files[i] ?? new byte[100];
            r.Packets.Add(new Par2FileDescriptionPacket(s, id, Md5Hash.Compute(data), default, data.Length, $"f{i}"));
            if (files[i] == null)
            {
                continue; // no checksums for this file
            }

            var slices = new List<(Md5Hash, uint)>();
            for (int o = 0; o < data.Length; o += SliceSize)
            {
                var padded = Padded(data.AsSpan(o, Math.Min(SliceSize, data.Length - o)));
                slices.Add((Md5Hash.Compute(padded), Crc32Ieee.Compute(padded)));
            }

            r.Packets.Add(new Par2SliceChecksumPacket(s, id, slices));
        }

        r.Packets.Add(new Par2MainPacket(s, SliceSize, ids, []));
        return Par2RecoverySet.Assemble([r])!;
    }

    private static byte[] Padded(ReadOnlySpan<byte> data)
    {
        var p = new byte[SliceSize];
        data.CopyTo(p);
        return p;
    }

    private static int Full(Par2SliceMatcher m, ReadOnlySpan<byte> window) => m.MatchFull(m.Weak(window), window);

    [TestMethod]
    public void FullSlices_AreFoundByCrcAndMd5()
    {
        var a = TestData.Random(3 * SliceSize, 1);
        var m = new Par2SliceMatcher(SetOf(a));
        Assert.AreEqual(SliceSize, m.BlockSize);
        for (int s = 0; s < 3; s++)
        {
            var window = a.AsSpan(s * SliceSize, SliceSize);
            Assert.IsTrue(m.MayMatch(m.Weak(window)));
            Assert.AreEqual(s, Full(m, window));
        }

        Assert.AreEqual(-1, Full(m, TestData.Random(SliceSize, 2)), "unknown CRC");
        Assert.AreEqual(0, m.TailLengths.Count, "the last slice is full");
    }

    [TestMethod]
    public void CrcCollision_IsRejectedByMd5()
    {
        var a = TestData.Random(SliceSize, 3);
        var m = new Par2SliceMatcher(SetOf(a));
        var fake = TestData.Random(SliceSize, 4);
        CrcForge.Force(fake, 10, Crc32Ieee.Compute(a));
        Assert.AreEqual(Crc32Ieee.Compute(a), Crc32Ieee.Compute(fake));
        Assert.IsTrue(m.MayMatch(m.Weak(fake)));
        Assert.AreEqual(-1, Full(m, fake));
    }

    [TestMethod]
    public void Slide_GivesTheCrcOfTheNextWindow()
    {
        var data = TestData.Random(SliceSize + 50, 5);
        var m = new Par2SliceMatcher(SetOf(data));
        ulong weak = m.Weak(data.AsSpan(0, SliceSize));
        for (int i = 1; i <= 50; i++)
        {
            weak = m.Slide(weak, data[i - 1], data[i + SliceSize - 1]);
            Assert.AreEqual(m.Weak(data.AsSpan(i, SliceSize)), weak);
        }
    }

    [TestMethod]
    public void MayMatch_FiltersMostOtherCrcs()
    {
        var m = new Par2SliceMatcher(SetOf(TestData.Random(10 * SliceSize, 6)));
        var rnd = new Random(7);
        int passed = Enumerable.Range(0, 2000).Count(_ => m.MayMatch((uint)rnd.NextInt64(0, 1L << 32)));
        Assert.IsTrue(passed < 20, $"{passed} of 2000 random CRCs passed a filter holding 10");
    }

    [TestMethod]
    public void Tails_AreMatchedZeroPadded()
    {
        var a = TestData.Random(SliceSize + 20, 8);
        var b = TestData.Random(30, 9);
        var m = new Par2SliceMatcher(SetOf(a, b));
        CollectionAssert.AreEquivalent(new[] { 20, 30 }, m.TailLengths.ToArray());
        Assert.AreEqual(1, m.MatchTail(a.AsSpan(SliceSize)));
        Assert.AreEqual(2, m.MatchTail(b));
        Assert.AreEqual(-1, m.MatchTail(b.AsSpan(0, 25)), "no tail of that length");
        Assert.AreEqual(-1, m.MatchTail(TestData.Random(30, 10)), "tail length, other CRC");

        // Same length and same CRC of the padded slice, different contents.
        var fake = Padded(TestData.Random(30, 11));
        CrcForge.Force(fake, 0, Crc32Ieee.Compute(Padded(b)));
        Assert.AreEqual(-1, m.MatchTail(fake.AsSpan(0, 30)));
    }

    [TestMethod]
    public void IdenticalSlices_FormOneGroup()
    {
        var s = TestData.Random(SliceSize, 12);
        var other = TestData.Random(SliceSize, 13);
        var m = new Par2SliceMatcher(SetOf([.. s, .. other, .. s], [.. s]));
        CollectionAssert.AreEqual(new[] { 0, 2, 3 }, m.SlicesWithSameContent(0).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 2, 3 }, m.SlicesWithSameContent(3).ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, m.SlicesWithSameContent(1).ToArray());
        Assert.AreEqual(0, Full(m, s), "the first slice of a group represents it");
    }

    [TestMethod]
    public void FilesWithoutChecksums_AreNotSearchedFor()
    {
        var a = TestData.Random(SliceSize, 14);
        var set = SetOf(null, a);
        Assert.IsNull(set.Files[0].SliceChecksums);
        var m = new Par2SliceMatcher(set);
        Assert.AreEqual(2, Full(m, a), "file 0 (100 bytes, 2 slices) has no checksums, file 1 starts at slice 2");
        Assert.AreEqual(-1, Full(m, new byte[SliceSize]));
    }
}

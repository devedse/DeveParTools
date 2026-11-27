using DeveParTools.Core.Galois;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3.Tests;

/// <summary>Field selection, Cauchy coefficients (both conventions), the PCG generator and sparse matrices.</summary>
[TestClass]
public sealed class CodesTests
{
    /// <summary>Reports which field implementation <see cref="Par3Fields.With"/> picked, and checks it is a field.</summary>
    private sealed class Probe : IFieldAction<string>
    {
        public string Run<T>(IGaloisField<T> field) where T : IEquatable<T>
        {
            // a · a⁻¹ = 1 for a few elements: holds for every element of a field, fails for most when the generator is reducible.
            var rnd = new Random(1);
            var bytes = new byte[field.SymbolSize];
            for (int i = 0; i < 20; i++)
            {
                rnd.NextBytes(bytes);
                var a = field.Read(bytes);
                if (!field.IsZero(a))
                {
                    Assert.AreEqual(field.One, field.Mul(a, field.Inv(a)));
                }
            }

            return $"{field.GetType().Name}/{field.SymbolSize}";
        }
    }

    [TestMethod]
    [DataRow(new byte[] { 0x1D }, "Gf8Field/1", DisplayName = "GF(2^8) 0x11D: table field")]
    [DataRow(new byte[] { 0x0B, 0x10 }, "Gf16Field/2", DisplayName = "GF(2^16) 0x1100B: table field")]
    [DataRow(new byte[] { 0x1B }, "GfWideField/1", DisplayName = "GF(2^8) 0x11B (AES, not primitive): generic field")]
    [DataRow(new byte[] { 0x8D, 0, 0, 0 }, "GfWideField/4", DisplayName = "GF(2^32) x^32+x^7+x^3+x^2+1 (our creator's)")]
    [DataRow(new byte[] { 0x87, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "GfWideField/16", DisplayName = "GF(2^128) GCM polynomial")]
    public void With_PicksTheImplementationForTheGenerator(byte[] generator, string expected)
        => Assert.AreEqual(expected, Par3Fields.With(generator, new Probe()));

    [TestMethod]
    public void With_NonPrimitive16BitGenerator_UsesTheGenericField()
    {
        // 0x1002B is irreducible (a field) but not primitive, so the log-table field cannot be built for it.
        Assert.AreEqual("GfWideField/2", Par3Fields.With([0x2B, 0x00], new Probe()));
    }

    /// <summary>x^136 + x^5 + x^3 + x^2 + 1, irreducible (checked with Rabin's test): a 17-byte field.</summary>
    private static readonly byte[] Big136 = [0x2D, .. new byte[16]];

    [TestMethod]
    public void With_MoreThan16Bytes_UsesTheBigField()
        => Assert.AreEqual("GfBigField/17", Par3Fields.With(Big136, new Probe()));

    [TestMethod]
    public void With_SizeZero_IsXorWithoutFieldArithmetic()
        => Assert.ThrowsExactly<NotSupportedException>(() => Par3Fields.With([], new FieldName()));

    private sealed class FieldName : IFieldAction<string>
    {
        public string Run<T>(IGaloisField<T> field) where T : IEquatable<T> => $"{field.GetType().Name}/{field.SymbolSize}";
    }

    [TestMethod]
    public void IntegerBytes_And_MaxMinusBytes_AreLittleEndian()
    {
        CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 0, 0 }, Par3Fields.IntegerBytes(4, 0x1234));
        CollectionAssert.AreEqual(new byte[] { 0x34 }, Par3Fields.IntegerBytes(1, 0x1234));
        CollectionAssert.AreEqual(new byte[] { 0xFD, 0xFF }, Par3Fields.MaxMinusBytes(2, 2));

        // Wider than 8 bytes: the high bytes are 0 for I and all ones for MAX − R.
        var x = Par3Fields.IntegerBytes(12, ulong.MaxValue);
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)0xFF, 8).Concat(new byte[4]).ToArray(), x);
        var y = Par3Fields.MaxMinusBytes(12, 1);
        CollectionAssert.AreEqual(new byte[] { 0xFE }.Concat(Enumerable.Repeat((byte)0xFF, 11)).ToArray(), y);
    }

    [TestMethod]
    public void Cauchy_DocumentedExampleInGf8()
    {
        var f = new Gf8Field(GaloisField8.Default);

        // Specification (x_I = I + 1): input 0, recovery 0 → 1/(1 ^ 255) = 1/254; input 2, recovery 1 → 1/(3 ^ 254) = 1/253.
        Assert.AreEqual(f.Inv(254), Par3Cauchy.Coefficient(f, 0, 0, Par3CauchyConvention.Specification));
        Assert.AreEqual(f.Inv(253), Par3Cauchy.Coefficient(f, 1, 2, Par3CauchyConvention.Specification));

        // par3cmdline (x_I = I): input 0, recovery 0 → 1/(0 ^ 255) = 1/255; input 2, recovery 1 → 1/(2 ^ 254) = 1/252.
        Assert.AreEqual(f.Inv(255), Par3Cauchy.Coefficient(f, 0, 0));
        Assert.AreEqual(f.Inv(252), Par3Cauchy.Coefficient(f, 1, 2, Par3CauchyConvention.Par3cmdline));
    }

    [TestMethod]
    public void Cauchy_InEveryFieldWidth_IsTheInverseOfXPlusY()
    {
        foreach (var gen in new[] { new byte[] { 0x0B, 0x10 }, [0x8D, 0, 0, 0], [0x87, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], Big136 })
        {
            Par3Fields.With(gen, new CauchyCheck());
        }
    }

    private sealed class CauchyCheck : IFieldAction<bool>
    {
        public bool Run<T>(IGaloisField<T> field) where T : IEquatable<T>
        {
            int n = field.SymbolSize;
            foreach (var (r, i) in new[] { (0UL, 0UL), (3UL, 100UL), (7UL, 65_000UL) })
            {
                foreach (var convention in new[] { Par3CauchyConvention.Par3cmdline, Par3CauchyConvention.Specification })
                {
                    ulong xi = convention == Par3CauchyConvention.Specification ? i + 1 : i;
                    var sum = field.Add(field.Read(Par3Fields.IntegerBytes(n, xi)), field.Read(Par3Fields.MaxMinusBytes(n, r)));
                    Assert.AreEqual(field.One, field.Mul(sum, Par3Cauchy.Coefficient(field, r, i, convention)), $"{n} bytes, r={r}, i={i}, {convention}");
                }
            }

            return true;
        }
    }

    [TestMethod]
    [DataRow(0UL, 0UL, 0UL, true)]
    [DataRow(0UL, 0UL, ulong.MaxValue, true)]
    [DataRow(3UL, 6UL, 2UL, false)]
    [DataRow(3UL, 6UL, 3UL, true)]
    [DataRow(3UL, 6UL, 5UL, true)]
    [DataRow(3UL, 6UL, 6UL, false)]
    [DataRow(3UL, 0UL, 1_000_000UL, true)]
    [DataRow(3UL, 0UL, 2UL, false)]
    public void InRange_IsHalfOpenWithZeroZeroMeaningAll(ulong first, ulong lastPlus1, ulong input, bool expected)
        => Assert.AreEqual(expected, Par3Cauchy.InRange(first, lastPlus1, input));

    [TestMethod]
    [DataRow(0UL, new[] { 0xcbf98931523d4eefUL, 0x4d98b91b8d356870UL, 0x01070196e695f8f1UL, 0x703ec840c59f4493UL })]
    [DataRow(1UL, new[] { 0xedbead14b0e6ef47UL, 0xc81c079e90c62221UL, 0xe78c2ba5819e56dcUL, 0x8947dad444c2da35UL })]
    [DataRow(0x123456789ABCDEF0UL, new[] { 0x0087525b109c6623UL, 0xb2c423612ea9e8d6UL, 0x9c1095fca3f2edbeUL, 0xc16648cf8e3d1668UL })]
    public void Pcg64_MatchesAnIndependentImplementationOfTheSpecText(ulong seed, ulong[] expected)
    {
        // Reference values from a Python transcription of the spec: 128-bit state = seed, advance with the PCG default
        // 128-bit multiplier and increment, output = rotr64(high ^ low, high >> 58).
        var pcg = new Pcg64(seed);
        CollectionAssert.AreEqual(expected, expected.Select(_ => pcg.Next()).ToArray());
    }

    [TestMethod]
    public void Sparse_Gf8_MatchesReference()
    {
        // 3 input rows, 5 recovery columns, 2 non-zero values per row, seed 42 (same Python reference).
        var rows = Par3Sparse.Generate(new Gf8Field(GaloisField8.Default), new Par3SparseMatrixPacket(0, 0, 5, 2, 42), 3);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 13, 0, 154 }, rows[0]);
        CollectionAssert.AreEqual(new byte[] { 52, 0, 0, 0, 189 }, rows[1]);
        CollectionAssert.AreEqual(new byte[] { 21, 101, 0, 0, 0 }, rows[2]);
    }

    [TestMethod]
    public void Sparse_Gf16_AllNonZero_MatchesReference()
    {
        var rows = Par3Sparse.Generate(new Gf16Field(GaloisField16.Get(0x1100B)), new Par3SparseMatrixPacket(0, 0, 4, 4, 7), 2);
        CollectionAssert.AreEqual(new ushort[] { 53152, 7058, 41119, 19168 }, rows[0]);
        CollectionAssert.AreEqual(new ushort[] { 44292, 52599, 23866, 61230 }, rows[1]);
    }

    [TestMethod]
    public void Sparse_12ByteField_UsesTwoRandomValuesPerElement()
    {
        // B = 12 > 8: the first 8 bytes from one 64-bit value, the last 4 from the low bits of the next.
        var field = new GfWideField(12, new byte[12] { 0x8D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var rows = Par3Sparse.Generate(field, new Par3SparseMatrixPacket(0, 0, 3, 2, 99), 2);
        Assert.AreEqual(UInt128.Zero, rows[0][0]);
        Assert.AreEqual(UInt128.Parse("5f8f73e221afabdd5a3f5", System.Globalization.NumberStyles.HexNumber), rows[0][1]);
        Assert.AreEqual(UInt128.Parse("4f83dac89b03a1202af08426", System.Globalization.NumberStyles.HexNumber), rows[0][2]);
        Assert.AreEqual(UInt128.Parse("8644052e837193a3af9945ab", System.Globalization.NumberStyles.HexNumber), rows[1][2]);
    }

    [TestMethod]
    public void Sparse_RangeAndNonZeroCount()
    {
        var f = new Gf8Field(GaloisField8.Default);

        // Rows only for inputs [first, min(last, count)); X is capped at the column count.
        var rows = Par3Sparse.Generate(f, new Par3SparseMatrixPacket(2, 100, 6, 10, 5), 9);
        Assert.AreEqual(7, rows.Count);
        Assert.IsTrue(rows.All(r => r.Length == 6 && r.All(v => v != 0)));

        rows = Par3Sparse.Generate(f, new Par3SparseMatrixPacket(0, 0, 50, 3, 11), 40);
        Assert.AreEqual(40, rows.Count);
        Assert.IsTrue(rows.All(r => r.Count(v => v != 0) == 3));
        Assert.IsTrue(rows.Select(r => Array.FindIndex(r, v => v != 0)).Distinct().Count() > 5, "the shuffle spreads the non-zero values");

        // Deterministic per seed, different for other seeds.
        CollectionAssert.AreEqual(rows[7], Par3Sparse.Generate(f, new Par3SparseMatrixPacket(0, 0, 50, 3, 11), 40)[7]);
        CollectionAssert.AreNotEqual(rows[7], Par3Sparse.Generate(f, new Par3SparseMatrixPacket(0, 0, 50, 3, 12), 40)[7]);
    }
}

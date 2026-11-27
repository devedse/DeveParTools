using DeveParTools.Core.Codes;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Codes;

[TestClass]
public sealed class GfMatrixTests
{
    private static readonly Gf8Field F8 = new(GaloisField8.Default);

    [TestMethod]
    public void Constructor_RejectsNegativeDimensions_AllowsEmpty()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GfMatrix<byte>(-1, 2, F8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GfMatrix<byte>(2, -1, F8));
        var empty = new GfMatrix<byte>(0, 0, F8);
        Assert.IsTrue(empty.TryInvert(), "the 0×0 matrix is its own inverse");
        Assert.AreEqual(0, empty.Multiply(empty).Data.Length);
    }

    [TestMethod]
    public void Identity_IsTheMultiplicativeNeutralElement()
    {
        var id = GfMatrix<byte>.Identity(5, F8);
        Assert.AreEqual(5, id.Rows);
        Assert.AreEqual(5, id.Cols);
        Assert.AreSame(F8, id.Field);
        var m = RandomMatrix(5, 7, new Random(1));
        CollectionAssert.AreEqual(m.Data, id.Multiply(m).Data);
        CollectionAssert.AreEqual(m.Data, m.Multiply(GfMatrix<byte>.Identity(7, F8)).Data);

        var inv = id.Clone();
        Assert.IsTrue(inv.TryInvert());
        CollectionAssert.AreEqual(id.Data, inv.Data);
    }

    [TestMethod]
    public void Clone_IsIndependent()
    {
        var m = RandomMatrix(3, 3, new Random(2));
        var c = m.Clone();
        c[1, 1] ^= 0xFF;
        Assert.AreNotEqual(m[1, 1], c[1, 1]);
        Assert.AreEqual(m[0, 0], c[0, 0]);
    }

    [TestMethod]
    [DataRow(3, 4, 2)]
    [DataRow(70, 9, 65)] // ≥ 64 rows: rows are computed in parallel
    public void Multiply_MatchesTheDefinition(int rows, int inner, int cols)
    {
        var rnd = new Random(rows);
        var a = RandomMatrix(rows, inner, rnd);
        var b = RandomMatrix(inner, cols, rnd);
        var p = a.Multiply(b);
        Assert.AreEqual(rows, p.Rows);
        Assert.AreEqual(cols, p.Cols);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                byte acc = 0;
                for (int k = 0; k < inner; k++)
                {
                    acc ^= F8.Mul(a[r, k], b[k, c]);
                }

                Assert.AreEqual(acc, p[r, c], $"({r},{c})");
            }
        }
    }

    [TestMethod]
    public void TryInvert_PermutationMatrix_NeedsSeveralRowSwaps()
    {
        // A cyclic shift: every column's pivot is below the diagonal, so each step swaps rows, and the swaps are
        // undone as column swaps at the end. The inverse of a permutation matrix is its transpose.
        int n = 6;
        var p = new GfMatrix<byte>(n, n, F8);
        for (int r = 0; r < n; r++)
        {
            p[r, (r + 1) % n] = 1;
        }

        var inv = p.Clone();
        Assert.IsTrue(inv.TryInvert());
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                Assert.AreEqual(p[c, r], inv[r, c]);
            }
        }
    }

    [TestMethod]
    public void TryInvert_ZeroColumn_IsSingular()
    {
        var m = RandomMatrix(4, 4, new Random(3));
        for (int r = 0; r < 4; r++)
        {
            m[r, 2] = 0;
        }

        Assert.IsFalse(m.TryInvert());
    }

    [TestMethod]
    public void TryInvert_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var m = GfMatrix<byte>.Identity(3, F8);
        Assert.ThrowsExactly<OperationCanceledException>(() => m.TryInvert(cts.Token));
    }

    [TestMethod]
    public void ReferenceTypeElements_StartAsFieldZero()
    {
        // GfBigElement is a struct holding an array, so a fresh matrix must be filled with real zero elements.
        var f = new GfBigField(3, [0x1B, 0, 0]);
        var m = new GfMatrix<GfBigElement>(2, 3, f);
        Assert.IsTrue(m.Data.All(f.IsZero));
        Assert.IsTrue(m.Data.All(e => e.Bytes.Length == 3));

        var a = new GfMatrix<GfBigElement>(2, 2, f);
        a[0, 0] = f.FromUInt64(1);
        a[0, 1] = f.FromUInt64(2);
        a[1, 0] = f.FromUInt64(3);
        a[1, 1] = f.FromUInt64(4);
        var inv = a.Clone();
        Assert.IsTrue(inv.TryInvert());
        var id = a.Multiply(inv);
        Assert.AreEqual(f.One, id[0, 0]);
        Assert.AreEqual(f.Zero, id[0, 1]);
        Assert.AreEqual(f.Zero, id[1, 0]);
        Assert.AreEqual(f.One, id[1, 1]);
    }

    private static GfMatrix<byte> RandomMatrix(int rows, int cols, Random rnd)
    {
        var m = new GfMatrix<byte>(rows, cols, F8);
        rnd.NextBytes(m.Data);
        return m;
    }
}

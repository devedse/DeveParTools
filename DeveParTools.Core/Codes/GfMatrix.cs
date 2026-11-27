using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Codes;

/// <summary>Dense row-major matrix over a Galois field.</summary>
public sealed class GfMatrix<T> where T : IEquatable<T>
{
    private const int ParallelRowThreshold = 64;

    public GfMatrix(int rows, int cols, IGaloisField<T> field)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        Rows = rows;
        Cols = cols;
        Field = field;
        Data = new T[(long)rows * cols];
        if (System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Fill(Data, field.Zero);
        }
    }

    public int Rows { get; }

    public int Cols { get; }

    public IGaloisField<T> Field { get; }

    public T[] Data { get; }

    public T this[int row, int col]
    {
        get => Data[(long)row * Cols + col];
        set => Data[(long)row * Cols + col] = value;
    }

    public Span<T> Row(int row) => Data.AsSpan((int)((long)row * Cols), Cols);

    public static GfMatrix<T> Identity(int n, IGaloisField<T> field)
    {
        var m = new GfMatrix<T>(n, n, field);
        for (int i = 0; i < n; i++)
        {
            m[i, i] = field.One;
        }

        return m;
    }

    public GfMatrix<T> Clone()
    {
        var m = new GfMatrix<T>(Rows, Cols, Field);
        Data.CopyTo(m.Data, 0);
        return m;
    }

    public GfMatrix<T> SelectRows(IReadOnlyList<int> rows)
    {
        var m = new GfMatrix<T>(rows.Count, Cols, Field);
        for (int i = 0; i < rows.Count; i++)
        {
            Row(rows[i]).CopyTo(m.Row(i));
        }

        return m;
    }

    public GfMatrix<T> SelectColumns(IReadOnlyList<int> cols)
    {
        var m = new GfMatrix<T>(Rows, cols.Count, Field);
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < cols.Count; c++)
            {
                m[r, c] = this[r, cols[c]];
            }
        }

        return m;
    }

    /// <summary>this × other.</summary>
    public GfMatrix<T> Multiply(GfMatrix<T> other)
    {
        if (Cols != other.Rows)
        {
            throw new ArgumentException("Dimension mismatch.");
        }

        var result = new GfMatrix<T>(Rows, other.Cols, Field);
        void DoRow(int i)
        {
            var dst = result.Row(i);
            for (int k = 0; k < Cols; k++)
            {
                var a = this[i, k];
                if (!Field.IsZero(a))
                {
                    Field.RowMulAdd(dst, other.Row(k), a);
                }
            }
        }

        if (Rows >= ParallelRowThreshold)
        {
            Parallel.For(0, Rows, DoRow);
        }
        else
        {
            for (int i = 0; i < Rows; i++)
            {
                DoRow(i);
            }
        }

        return result;
    }

    /// <summary>In-place Gauss-Jordan inversion. Returns false (matrix left undefined) when singular.</summary>
    public bool TryInvert(CancellationToken cancellationToken = default)
    {
        if (Rows != Cols)
        {
            throw new InvalidOperationException("Only square matrices can be inverted.");
        }

        int n = Rows;
        var pivotRow = new int[n];
        for (int col = 0; col < n; col++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int p = col;
            while (p < n && Field.IsZero(this[p, col]))
            {
                p++;
            }

            if (p == n)
            {
                return false;
            }

            if (p != col)
            {
                var tmp = new T[n];
                Row(p).CopyTo(tmp);
                Row(col).CopyTo(Row(p));
                tmp.CopyTo(Row(col));
            }

            pivotRow[col] = p;
            var pivInv = Field.Inv(this[col, col]);
            this[col, col] = Field.One;
            Field.RowScale(Row(col), pivInv);

            int c = col;
            void Eliminate(int r)
            {
                if (r == c)
                {
                    return;
                }

                var f = this[r, c];
                if (Field.IsZero(f))
                {
                    return;
                }

                this[r, c] = Field.Zero;
                Field.RowMulAdd(Row(r), Row(c), f);
            }

            if (n >= ParallelRowThreshold)
            {
                Parallel.For(0, n, Eliminate);
            }
            else
            {
                for (int r = 0; r < n; r++)
                {
                    Eliminate(r);
                }
            }
        }

        // Undo the row interchanges as column interchanges, in reverse order.
        for (int col = n - 1; col >= 0; col--)
        {
            int p = pivotRow[col];
            if (p != col)
            {
                for (int r = 0; r < n; r++)
                {
                    (this[r, p], this[r, col]) = (this[r, col], this[r, p]);
                }
            }
        }

        return true;
    }
}

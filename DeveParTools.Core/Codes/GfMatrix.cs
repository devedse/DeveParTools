using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Codes;

/// <summary>
/// Dense row-major matrix over a Galois field, with in-place Gauss-Jordan inversion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why matrices?</b> Every PAR recovery block is a linear combination of the data blocks:
/// <c>recovery[j] = Σ_i C[j,i] · data[i]</c> (one symbol at a time, all arithmetic in the Galois field, where
/// "+" is XOR). When data blocks are lost, the recovery blocks we still have give us a system of linear
/// equations in the unknown blocks. Solving it means inverting the square sub-matrix that maps the missing
/// blocks to the recovery blocks we use.
/// </para>
/// <para>
/// <b>Field arithmetic reminder.</b> Addition and subtraction are both XOR, so "−x" is just "x". Every non-zero
/// element has a multiplicative inverse, so the usual school algorithm for inverting a matrix works unchanged:
/// there is no rounding and no numerical instability, any non-zero pivot is as good as any other.
/// </para>
/// </remarks>
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

    /// <summary>Row-major storage: element (r, c) is at <c>r * Cols + c</c>.</summary>
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

    /// <summary>New matrix made of the given rows (in the given order).</summary>
    public GfMatrix<T> SelectRows(IReadOnlyList<int> rows)
    {
        var m = new GfMatrix<T>(rows.Count, Cols, Field);
        for (int i = 0; i < rows.Count; i++)
        {
            Row(rows[i]).CopyTo(m.Row(i));
        }

        return m;
    }

    /// <summary>New matrix made of the given columns (in the given order).</summary>
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

    /// <summary>
    /// Matrix product <c>this × other</c>. Computed row by row: result row i is
    /// <c>Σ_k this[i,k] · other.Row(k)</c>, so each step is one SIMD "multiply a row by a constant and XOR it in"
    /// operation. Rows are processed in parallel for large matrices.
    /// </summary>
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

    /// <summary>
    /// Inverts the matrix in place using Gauss-Jordan elimination with row pivoting. Returns <c>false</c> when the
    /// matrix is singular (the contents are then undefined). Cost is n³ multiply-adds, done as n² SIMD row
    /// operations; for n ≥ 64 the eliminations of each column run in parallel.
    /// </summary>
    /// <remarks>
    /// <para><b>The in-place trick.</b> The textbook method appends the identity matrix, <c>[A | I]</c>, and
    /// row-reduces until it becomes <c>[I | A⁻¹]</c>. That needs twice the memory. Because column <c>c</c> of the
    /// left half becomes a unit vector exactly when column <c>c</c> of the right half starts to fill in, the two can
    /// share storage: after processing column <c>c</c> we store the right-half values in the slot that just became
    /// "known" (0 or 1). Concretely, for pivot column <c>c</c>:</para>
    /// <list type="number">
    /// <item>find a row <c>p ≥ c</c> with a non-zero entry in column <c>c</c> and swap it into row <c>c</c>
    /// (remember the swap);</item>
    /// <item>replace the pivot by 1, then multiply row <c>c</c> by <c>1/pivot</c> (so the pivot slot ends up
    /// holding <c>1/pivot</c>, the right-half value);</item>
    /// <item>for every other row <c>r</c>: let <c>f = A[r,c]</c>, set <c>A[r,c] = 0</c>, then add <c>f · row c</c>
    /// to row <c>r</c> (so the slot ends up holding <c>f/pivot</c>, again the right-half value).</item>
    /// </list>
    /// <para>Finally, every row swap done in step 1 is undone as a <i>column</i> swap, in reverse order.</para>
    /// <para><b>Worked example</b> in GF(2^8) with generator 0x11D (where <c>2 · 0x8E = 1</c>, so
    /// <c>1/2 = 0x8E</c>), for <c>A = [[1, 2], [3, 4]]</c>:</para>
    /// <code>
    /// column 0: pivot A[0,0] = 1, 1/1 = 1      row0 = [1, 2]
    ///           row1: f = 3, A[1,0] := 0, row1 ^= 3·row0 = [0^3, 4^(3·2)] = [3, 4^6] = [3, 2]
    /// column 1: pivot A[1,1] = 2, 1/2 = 0x8E   A[1,1] := 1, row1 = 0x8E·[3, 1] = [0x8F, 0x8E]
    ///           row0: f = 2, A[0,1] := 0, row0 ^= 2·row1 = [1^(2·0x8F), 0^(2·0x8E)] = [1^3, 1] = [2, 1]
    /// result:   A⁻¹ = [[2, 1], [0x8F, 0x8E]]
    /// check:    A·A⁻¹ row 0 = [1·2 ^ 2·0x8F, 1·1 ^ 2·0x8E] = [2^3, 1^1] = [1, 0]   ✓
    /// </code>
    /// <para><b>Why pivoting?</b> <c>[[0, 1], [1, 0]]</c> has a zero in the top-left corner, so row 1 is swapped
    /// into row 0 first. In the in-place scheme that row swap turns into a column swap of the result at the end.
    /// Unlike floating point, any non-zero pivot is exact, so we simply take the first one.</para>
    /// </remarks>
    /// <param name="progress">Called with the fraction of columns done (every 1%), for progress of large inversions.</param>
    public bool TryInvert(CancellationToken cancellationToken = default, Action<double>? progress = null)
    {
        if (Rows != Cols)
        {
            throw new InvalidOperationException("Only square matrices can be inverted.");
        }

        int n = Rows;
        var pivotRow = new int[n];
        var swapBuffer = new T[n];
        int step = Math.Max(1, n / 100);
        for (int col = 0; col < n; col++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (col % step == 0)
            {
                progress?.Invoke((double)col / n);
            }


            // Step 1: find a usable (non-zero) pivot at or below the diagonal.
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
                Row(p).CopyTo(swapBuffer);
                Row(col).CopyTo(Row(p));
                swapBuffer.CopyTo(Row(col));
            }

            pivotRow[col] = p;

            // Step 2: scale the pivot row; the pivot slot becomes 1/pivot.
            var pivInv = Field.Inv(this[col, col]);
            this[col, col] = Field.One;
            Field.RowScale(Row(col), pivInv);

            // Step 3: eliminate column 'col' from every other row; the slot receives the right-half value.
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

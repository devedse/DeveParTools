using DeveParTools.Core.Galois;

namespace DeveParTools.Par2;

/// <summary>
/// The PAR 2.0 recovery code: Reed-Solomon-style over GF(2^16) with generator 0x1100B.
/// </summary>
/// <remarks>
/// <para><b>The formula.</b> Every input slice <c>i</c> (in set order: files sorted by File ID, then slices in file
/// order) gets a constant <c>base_i</c>. A recovery slice with exponent <c>e</c> is, 16-bit word by 16-bit word,</para>
/// <code>R_e = Σ_i base_i^e · D_i</code>
/// <para>where all arithmetic is in GF(2^16) ("+" is XOR).</para>
/// <para><b>The constants.</b> <c>base_i = α^n_i</c> with α = 2 and <c>n_i</c> the i-th exponent that is not a
/// multiple of 3, 5, 17 or 257. Those primes are the factors of 65535 = 3·5·17·257, so every chosen constant has
/// the full multiplicative order 65535. There are exactly φ(65535) = 32768 such constants, which is why PAR2 allows
/// at most 32768 input slices. The first ones are:</para>
/// <code>
/// n_i    : 1  2   4    7    8    11    13     14     16     19     22 ...
/// base_i : 2  4  16  128  256  2048  8192  16384   4107  32856  17132 ...
/// </code>
/// <para><b>Worked example</b> with three input slices of one word each, D = (D0, D1, D2):</para>
/// <code>
/// R_0 = 1·D0 ^ 1·D1 ^ 1·D2                (exponent 0: plain XOR parity)
/// R_1 = 2·D0 ^ 4·D1 ^ 16·D2
/// R_2 = 4·D0 ^ 16·D1 ^ 256·D2             (2² = 4, 4² = 16, 16² = 256)
/// </code>
/// <para><b>Caveat.</b> This is a Vandermonde-like matrix, not a systematic Reed-Solomon code, so a square sub-matrix
/// (some missing slices × some exponents) is not guaranteed to be invertible. In practice failures are very rare;
/// the repairer then simply tries another selection of recovery slices.</para>
/// </remarks>
public static class Par2Math
{
    public const int MaxInputSlices = 32768;

    public static GaloisField16 Field => GaloisField16.Par2;

    private static readonly ushort[] Bases = BuildBases();

    private static ushort[] BuildBases()
    {
        var f = GaloisField16.Par2;
        var bases = new ushort[MaxInputSlices];
        int n = 0;
        for (int i = 0; i < MaxInputSlices; i++)
        {
            do
            {
                n++;
            }
            while (n % 3 == 0 || n % 5 == 0 || n % 17 == 0 || n % 257 == 0);

            bases[i] = f.Exp(n);
        }

        return bases;
    }

    /// <summary>The constant of input slice <paramref name="sliceIndex"/> (0-based, set order).</summary>
    public static ushort Base(int sliceIndex) => Bases[sliceIndex];

    /// <summary>Coefficient of input slice <paramref name="sliceIndex"/> in the recovery slice with <paramref name="exponent"/>.</summary>
    public static ushort Coefficient(int sliceIndex, uint exponent) => Field.Pow(Bases[sliceIndex], exponent);
}

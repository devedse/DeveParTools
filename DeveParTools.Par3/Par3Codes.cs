using System.Buffers.Binary;
using DeveParTools.Core.Galois;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

/// <summary>Callback invoked with the concrete field type of a PAR3 set (visitor over the element type).</summary>
public interface IFieldAction<out TResult>
{
    TResult Run<T>(IGaloisField<T> field) where T : IEquatable<T>;
}

/// <summary>
/// Maps the Galois field of a Start packet (size in bytes + generator without its leading 1) to an implementation:
/// fast SIMD fields for primitive 8/16-bit generators, generic carry-less fields otherwise (PAR3 requires every
/// byte-multiple field size).
/// </summary>
public static class Par3Fields
{
    public static TResult With<TResult>(ReadOnlySpan<byte> generator, IFieldAction<TResult> action)
    {
        int n = generator.Length;
        if (n == 1 && TryPrimitive8(0x100 | generator[0], out var f8))
        {
            return action.Run(new Gf8Field(f8));
        }

        if (n == 2 && TryPrimitive16(0x10000 | BinaryPrimitives.ReadUInt16LittleEndian(generator), out var f16))
        {
            return action.Run(new Gf16Field(f16));
        }

        if (n >= 1 && n <= 16)
        {
            return action.Run(new GfWideField(n, generator));
        }

        if (n > 16)
        {
            return action.Run(new GfBigField(n, generator));
        }

        throw new NotSupportedException("A Galois field of size 0 (plain XOR) has no field arithmetic.");
    }

    private static bool TryPrimitive8(int poly, out GaloisField8 field)
    {
        try
        {
            field = GaloisField8.Get(poly);
            return true;
        }
        catch (ArgumentException)
        {
            field = null!;
            return false;
        }
    }

    private static bool TryPrimitive16(int poly, out GaloisField16 field)
    {
        try
        {
            field = GaloisField16.Get(poly);
            return true;
        }
        catch (ArgumentException)
        {
            field = null!;
            return false;
        }
    }

    /// <summary>Little-endian bytes of the integer value v in a field of <paramref name="size"/> bytes.</summary>
    internal static byte[] IntegerBytes(int size, ulong v)
    {
        var b = new byte[size];
        for (int i = 0; i < Math.Min(8, size); i++)
        {
            b[i] = (byte)(v >> (8 * i));
        }

        return b;
    }

    /// <summary>Little-endian bytes of MAX − r, where MAX is the all-ones value of the field width.</summary>
    internal static byte[] MaxMinusBytes(int size, ulong r)
    {
        var b = new byte[size];
        Array.Fill(b, (byte)0xFF);
        // MAX - r: subtract r from all-ones = bitwise NOT of r within the low bytes (no borrow, since MAX has all bits set).
        for (int i = 0; i < Math.Min(8, size); i++)
        {
            b[i] = (byte)~(byte)(r >> (8 * i));
        }

        return b;
    }
}

/// <summary>How x_I is derived from the input block index I in the Cauchy matrix.</summary>
public enum Par3CauchyConvention
{
    /// <summary>x_I = I, as implemented by par3cmdline (the reference client). Default for interoperability.</summary>
    Par3cmdline,

    /// <summary>x_I = I + 1, as written in the PAR3 specification draft.</summary>
    Specification,
}

/// <summary>
/// PAR3 Cauchy matrix: element (recovery R, input I) = 1 / (x_I + y_R) with y_R = MAX − R and x_I = I + 1 (spec
/// text) or x_I = I (par3cmdline); integers are reinterpreted as field elements and "+" is XOR.
/// </summary>
/// <remarks>
/// <para>Why it works: in a Cauchy matrix every square sub-matrix is invertible as long as all x are distinct, all
/// y are distinct, and no x equals a y. With x_I = I+1 counting up from 1 and y_R = MAX−R counting down from the top
/// of the field, that holds while (blocks + recovery blocks) &lt; 2^bits − 1. So any L lost blocks can be rebuilt
/// from any L recovery blocks: an MDS (Reed-Solomon) code.</para>
/// <para>Example in GF(2^8) (MAX = 255): input 0, recovery 0 → 1/(1 ^ 255) = 1/254; input 2, recovery 1 →
/// 1/(3 ^ 254) = 1/253.</para>
/// </remarks>
public static class Par3Cauchy
{
    public static T Coefficient<T>(IGaloisField<T> field, ulong recovery, ulong input, Par3CauchyConvention convention = Par3CauchyConvention.Par3cmdline) where T : IEquatable<T>
    {
        ulong xValue = convention == Par3CauchyConvention.Specification ? input + 1 : input;
        var x = field.Read(Par3Fields.IntegerBytes(field.SymbolSize, xValue));
        var y = field.Read(Par3Fields.MaxMinusBytes(field.SymbolSize, recovery));
        return field.Inv(field.Add(x, y));
    }

    /// <summary>True when input index I lies in the packet's half-open range (0, 0 means all inputs).</summary>
    public static bool InRange(ulong first, ulong lastPlus1, ulong input)
        => (first == 0 && lastPlus1 == 0) || (input >= first && (lastPlus1 == 0 || input < lastPlus1));
}

/// <summary>
/// PCG-XSL-RR 128/64 random generator as specified for the PAR3 Sparse Random Matrix packet: 128-bit LCG state
/// seeded with the 64-bit seed in the low bits, output = rotate_right(high ^ low, high >> 58).
/// </summary>
/// <remarks>
/// The specification does not state the increment or whether the state advances before or after producing output.
/// This implementation uses the reference PCG library's defaults for the 128-bit "setseq" generator (default
/// multiplier and increment, advance-then-output). No other client implements sparse matrices yet, so this choice
/// cannot be cross-checked.
/// </remarks>
public sealed class Pcg64
{
    private static readonly UInt128 Multiplier = new(0x2360ED051FC65DA4UL, 0x4385DF649FCCF645UL);
    private static readonly UInt128 Increment = new(0x5851F42D4C957F2DUL, 0x14057B7EF767814FUL);
    private UInt128 _state;

    public Pcg64(ulong seed) => _state = seed;

    public ulong Next()
    {
        _state = unchecked(_state * Multiplier + Increment);
        ulong hi = (ulong)(_state >> 64);
        ulong lo = (ulong)_state;
        int rot = (int)(hi >> 58);
        return System.Numerics.BitOperations.RotateRight(hi ^ lo, rot);
    }
}

/// <summary>PAR3 Sparse Random matrix generation.</summary>
public static class Par3Sparse
{
    /// <summary>
    /// Non-zero coefficients per input block: element [input − first][recovery]. Rows are generated in input order:
    /// fill the row with C−X zeros and X random non-zero field values, then shuffle the non-zero part into place
    /// with the "inside-out" Fisher-Yates algorithm.
    /// </summary>
    public static List<T[]> Generate<T>(IGaloisField<T> field, Par3SparseMatrixPacket packet, ulong inputCount) where T : IEquatable<T>
    {
        ulong first = packet.First;
        ulong last = packet.LastPlus1 == 0 ? inputCount : Math.Min(packet.LastPlus1, inputCount);
        int c = (int)packet.MaxRecovery;
        int x = (int)Math.Min(packet.NonZeroPerInput, (ulong)c);
        var rng = new Pcg64(packet.Seed);
        var rows = new List<T[]>();
        int size = field.SymbolSize;
        var bytes = new byte[size];
        for (ulong input = first; input < last; input++)
        {
            var row = new T[c];
            for (int i = 0; i < c - x; i++)
            {
                row[i] = field.Zero;
            }

            for (int i = c - x; i < c; i++)
            {
                T v;
                do
                {
                    for (int b = 0; b < size; b += 8)
                    {
                        ulong r = rng.Next();
                        for (int k = 0; k < 8 && b + k < size; k++)
                        {
                            bytes[b + k] = (byte)(r >> (8 * k));
                        }
                    }

                    v = field.Read(bytes);
                }
                while (field.IsZero(v));

                row[i] = v;
            }

            for (int i = c - x; i < c; i++)
            {
                int j = (int)(rng.Next() % (ulong)(i + 1));
                (row[i], row[j]) = (row[j], row[i]);
            }

            rows.Add(row);
        }

        return rows;
    }
}

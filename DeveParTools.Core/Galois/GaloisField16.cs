using System.Collections.Concurrent;

namespace DeveParTools.Core.Galois;

/// <summary>
/// GF(2^16) defined by a generator polynomial (including the x^16 term, e.g. 0x1100B for PAR2).
/// Elements are stored as little-endian 16-bit values in polynomial basis.
/// </summary>
public sealed class GaloisField16 : IGf16Multiplier
{
    public const int Bits = 16;
    public const int Order = 1 << Bits;
    public const int Limit = Order - 1;

    private static readonly ConcurrentDictionary<int, GaloisField16> Cache = new();

    /// <summary>The field used by PAR2 (generator 0x1100B), also the PAR3 "common" 16-bit field.</summary>
    public static GaloisField16 Par2 => Get(0x1100B);

    public int Polynomial { get; }

    internal readonly ushort[] LogTable;
    // Exp table is doubled so Exp[Log[a] + Log[b]] never needs a modulo.
    internal readonly ushort[] ExpTable;

    private GaloisField16(int polynomial)
    {
        if ((polynomial >> Bits) != 1)
        {
            throw new ArgumentException($"Polynomial 0x{polynomial:X} must have degree {Bits}.", nameof(polynomial));
        }

        Polynomial = polynomial;
        LogTable = new ushort[Order];
        ExpTable = new ushort[Limit * 2 + 2];

        int x = 1;
        for (int i = 0; i < Limit; i++)
        {
            if (i != 0 && x == 1)
            {
                throw new ArgumentException($"Polynomial 0x{polynomial:X} is not primitive (x has order {i}).", nameof(polynomial));
            }

            ExpTable[i] = (ushort)x;
            ExpTable[i + Limit] = (ushort)x;
            LogTable[x] = (ushort)i;
            x <<= 1;
            if ((x & Order) != 0)
            {
                x ^= polynomial;
            }
        }

        if (x != 1)
        {
            throw new ArgumentException($"Polynomial 0x{polynomial:X} is not primitive.", nameof(polynomial));
        }

        ExpTable[Limit * 2] = ExpTable[0];
        ExpTable[Limit * 2 + 1] = ExpTable[1];
    }

    public static GaloisField16 Get(int polynomial) => Cache.GetOrAdd(polynomial, static p => new GaloisField16(p));

    public ushort Mul(ushort a, ushort b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }

        return ExpTable[LogTable[a] + LogTable[b]];
    }

    public ushort Div(ushort a, ushort b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("Division by zero in GF(2^16).");
        }

        if (a == 0)
        {
            return 0;
        }

        return ExpTable[LogTable[a] + Limit - LogTable[b]];
    }

    public ushort Inv(ushort a)
    {
        if (a == 0)
        {
            throw new DivideByZeroException("Zero has no inverse in GF(2^16).");
        }

        return ExpTable[Limit - LogTable[a]];
    }

    /// <summary>a^e for any non-negative exponent.</summary>
    public ushort Pow(ushort a, long e)
    {
        if (e == 0)
        {
            return 1;
        }

        if (a == 0)
        {
            return 0;
        }

        long l = LogTable[a] * (e % Limit) % Limit;
        return ExpTable[l];
    }

    /// <summary>The generator raised to the given power (alpha^i).</summary>
    public ushort Exp(int i) => ExpTable[((i % Limit) + Limit) % Limit];

    /// <summary>Discrete logarithm base alpha. Undefined for zero.</summary>
    public int Log(ushort a)
    {
        if (a == 0)
        {
            throw new ArgumentException("Log of zero is undefined.", nameof(a));
        }

        return LogTable[a];
    }
}

using System.Collections.Concurrent;

namespace DeveParTools.Core.Galois;

/// <summary>
/// GF(2^8) defined by a generator polynomial including the x^8 term (e.g. 0x11D).
/// </summary>
public sealed class GaloisField8
{
    public const int Bits = 8;
    public const int Order = 1 << Bits;
    public const int Limit = Order - 1;

    private static readonly ConcurrentDictionary<int, GaloisField8> Cache = new();

    /// <summary>The common 8-bit field (generator 0x11D), as listed in the PAR3 specification.</summary>
    public static GaloisField8 Default => Get(0x11D);

    public int Polynomial { get; }

    internal readonly byte[] LogTable;
    internal readonly byte[] ExpTable;
    // Full 256x256 multiplication table: MulTable[a * 256 + b].
    internal readonly byte[] MulTable;

    private GaloisField8(int polynomial)
    {
        if ((polynomial >> Bits) != 1)
        {
            throw new ArgumentException($"Polynomial 0x{polynomial:X} must have degree {Bits}.", nameof(polynomial));
        }

        Polynomial = polynomial;
        LogTable = new byte[Order];
        ExpTable = new byte[Limit * 2 + 2];

        int x = 1;
        for (int i = 0; i < Limit; i++)
        {
            if (i != 0 && x == 1)
            {
                throw new ArgumentException($"Polynomial 0x{polynomial:X} is not primitive.", nameof(polynomial));
            }

            ExpTable[i] = (byte)x;
            ExpTable[i + Limit] = (byte)x;
            LogTable[x] = (byte)i;
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

        MulTable = new byte[Order * Order];
        for (int a = 1; a < Order; a++)
        {
            for (int b = 1; b < Order; b++)
            {
                MulTable[(a << 8) | b] = ExpTable[LogTable[a] + LogTable[b]];
            }
        }
    }

    public static GaloisField8 Get(int polynomial) => Cache.GetOrAdd(polynomial, static p => new GaloisField8(p));

    public byte Mul(byte a, byte b) => MulTable[(a << 8) | b];

    public byte Div(byte a, byte b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("Division by zero in GF(2^8).");
        }

        if (a == 0)
        {
            return 0;
        }

        return ExpTable[LogTable[a] + Limit - LogTable[b]];
    }

    public byte Inv(byte a)
    {
        if (a == 0)
        {
            throw new DivideByZeroException("Zero has no inverse in GF(2^8).");
        }

        return ExpTable[Limit - LogTable[a]];
    }

    public byte Pow(byte a, long e)
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

    public byte Exp(int i) => ExpTable[((i % Limit) + Limit) % Limit];

    public int Log(byte a)
    {
        if (a == 0)
        {
            throw new ArgumentException("Log of zero is undefined.", nameof(a));
        }

        return LogTable[a];
    }
}

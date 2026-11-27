using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Codes.Fft;

/// <summary>
/// The finite fields used by the Leopard-RS FFT Reed-Solomon codec (GF(2^8) with generator 0x11D and GF(2^16)
/// with generator 0x1002D), in Leopard's <i>Cantor basis</i> representation.
/// </summary>
/// <remarks>
/// <para><b>What is a Cantor basis?</b> A 16-bit symbol is normally read as a polynomial: bit i is the coefficient of
/// x^i. Leopard instead reads bit i as the coefficient of a special basis element β_i (with β_0 = 1 and
/// β_i² + β_i = β_{i-1}). With this basis, the evaluation points of the additive FFT are simply the integers 0, 1,
/// 2, ... and the subspaces the FFT recurses on are "all integers below 2^k", which makes the formal derivative in
/// the decoder almost free. Addition is still XOR; only multiplication differs from the polynomial basis.</para>
/// <para><b>How tables are built.</b> <c>LogLUT[x]</c> is the discrete logarithm (base the field generator) of the
/// element whose Cantor coordinates are the bits of x; <c>ExpLUT</c> is its inverse. Multiplication is then the
/// usual <c>Exp[(Log[a] + Log[b]) mod 2^bits−1]</c>.</para>
/// <para>This is a port of Christopher A. Taylor's Leopard-RS (BSD-3-Clause), producing bit-identical results,
/// which is what par3cmdline's "PAR FFT" matrix packet relies on.</para>
/// </remarks>
public sealed class LeopardField : IGf16Multiplier, IGf8Multiplier
{
    public static LeopardField FF8 { get; } = new(8, 0x11D, [1, 214, 152, 146, 86, 200, 88, 230]);

    public static LeopardField FF16 { get; } = new(16, 0x1002D,
    [
        0x0001, 0xACCA, 0x3C0E, 0x163E, 0xC582, 0xED2E, 0x914C, 0x4012,
        0x6C98, 0x10D8, 0x6A72, 0xB900, 0xFDB8, 0xFB34, 0xFF38, 0x991E,
    ]);

    private readonly int _mask;

    private LeopardField(int bits, int polynomial, int[] cantorBasis)
    {
        Bits = bits;
        Order = 1 << bits;
        Modulus = Order - 1;
        Polynomial = polynomial;
        _mask = Order - 1;
        LogLut = new int[Order];
        ExpLut = new int[Order];
        FftSkew = new int[Modulus];
        LogWalsh = new int[Order];
        InitializeLogarithmTables(cantorBasis);
        FftInitialize();
    }

    public int Bits { get; }

    /// <summary>Number of field elements (2^bits).</summary>
    public int Order { get; }

    /// <summary>2^bits − 1: the multiplicative group order. A "log" equal to Modulus means "multiply by 0" in the FFT.</summary>
    public int Modulus { get; }

    public int Polynomial { get; }

    public int SymbolSize => Bits / 8;

    internal int[] LogLut { get; }

    internal int[] ExpLut { get; }

    /// <summary>Twisted factors (as logarithms) used by every FFT/IFFT layer.</summary>
    internal int[] FftSkew { get; }

    /// <summary>FWHT of the logarithm table, used to evaluate the error locator polynomial.</summary>
    internal int[] LogWalsh { get; }

    /// <summary>(a + b) mod Modulus with Leopard's partial reduction (the result may equal Modulus).</summary>
    internal int AddMod(int a, int b)
    {
        uint sum = (uint)a + (uint)b;
        return (int)((sum + (sum >> Bits)) & (uint)_mask);
    }

    /// <summary>(a − b) mod Modulus with Leopard's partial reduction.</summary>
    internal int SubMod(int a, int b)
    {
        uint dif = (uint)a - (uint)b;
        return (int)((dif + (dif >> Bits)) & (uint)_mask);
    }

    /// <summary>a · exp(logB): a multiplication whose right operand is already a logarithm.</summary>
    internal int MultiplyLog(int a, int logB) => a == 0 ? 0 : ExpLut[AddMod(LogLut[a], logB)];

    /// <summary>The element whose logarithm is <paramref name="log"/> (log == Modulus is treated as 0, i.e. 1).</summary>
    public int Exp(int log) => ExpLut[log];

    public int Log(int element) => LogLut[element];

    public int Mul(int a, int b) => a == 0 || b == 0 ? 0 : ExpLut[AddMod(LogLut[a], LogLut[b])];

    ushort IGf16Multiplier.Mul(ushort a, ushort b) => (ushort)Mul(a, b);

    byte IGf8Multiplier.Mul(byte a, byte b) => (byte)Mul(a, b);

    private void InitializeLogarithmTables(int[] cantorBasis)
    {
        // LFSR: ExpLut temporarily holds the polynomial-basis logarithm table.
        int state = 1;
        for (int i = 0; i < Modulus; i++)
        {
            ExpLut[state] = i;
            state <<= 1;
            if (state >= Order)
            {
                state ^= Polynomial;
            }
        }

        ExpLut[0] = Modulus;

        // Cantor-coordinate value → polynomial-basis value, then → logarithm.
        LogLut[0] = 0;
        for (int i = 0; i < Bits; i++)
        {
            int basis = cantorBasis[i];
            int width = 1 << i;
            for (int j = 0; j < width; j++)
            {
                LogLut[j + width] = LogLut[j] ^ basis;
            }
        }

        for (int i = 0; i < Order; i++)
        {
            LogLut[i] = ExpLut[LogLut[i]];
        }

        for (int i = 0; i < Order; i++)
        {
            ExpLut[LogLut[i]] = i;
        }

        ExpLut[Modulus] = ExpLut[0];
    }

    private void FftInitialize()
    {
        var temp = new int[Bits - 1];
        for (int i = 1; i < Bits; i++)
        {
            temp[i - 1] = 1 << i;
        }

        for (int m = 0; m < Bits - 1; m++)
        {
            int step = 1 << (m + 1);
            FftSkew[(1 << m) - 1] = 0;
            for (int i = m; i < Bits - 1; i++)
            {
                int s = 1 << (i + 1);
                for (int j = (1 << m) - 1; j < s; j += step)
                {
                    FftSkew[j + s] = FftSkew[j] ^ temp[i];
                }
            }

            temp[m] = Modulus - LogLut[MultiplyLog(temp[m], LogLut[temp[m] ^ 1])];
            for (int i = m + 1; i < Bits - 1; i++)
            {
                int sum = AddMod(LogLut[temp[i] ^ 1], temp[m]);
                temp[i] = MultiplyLog(temp[i], sum);
            }
        }

        for (int i = 0; i < Modulus; i++)
        {
            FftSkew[i] = LogLut[FftSkew[i]];
        }

        for (int i = 0; i < Order; i++)
        {
            LogWalsh[i] = LogLut[i];
        }

        LogWalsh[0] = 0;
        Fwht(LogWalsh, Order, Order);
    }

    /// <summary>
    /// In-place fast Walsh-Hadamard transform modulo <see cref="Modulus"/>: every butterfly maps (a, b) to
    /// (a + b, a − b). Only the first <paramref name="truncated"/> inputs may be non-zero.
    /// </summary>
    internal void Fwht(int[] data, int m, int truncated)
    {
        int dist = 1, dist4 = 4;
        for (; dist4 <= m; dist = dist4, dist4 <<= 2)
        {
            for (int r = 0; r < truncated; r += dist4)
            {
                for (int i = r; i < r + dist; i++)
                {
                    int s2 = dist << 1;
                    int t0 = data[i], t1 = data[i + dist], t2 = data[i + s2], t3 = data[i + s2 + dist];
                    Fwht2(ref t0, ref t1);
                    Fwht2(ref t2, ref t3);
                    Fwht2(ref t0, ref t2);
                    Fwht2(ref t1, ref t3);
                    data[i] = t0;
                    data[i + dist] = t1;
                    data[i + s2] = t2;
                    data[i + s2 + dist] = t3;
                }
            }
        }

        if (dist < m)
        {
            for (int i = 0; i < dist; i++)
            {
                int a = data[i], b = data[i + dist];
                Fwht2(ref a, ref b);
                data[i] = a;
                data[i + dist] = b;
            }
        }
    }

    private void Fwht2(ref int a, ref int b)
    {
        int sum = AddMod(a, b);
        int dif = SubMod(a, b);
        a = sum;
        b = dif;
    }
}

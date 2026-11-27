namespace DeveParTools.Core.Galois;

/// <summary>
/// Multiplication in some representation of GF(2^16). Region kernels only need "multiply by a constant", which is
/// a GF(2)-linear map on the 16 bits of a symbol, so any field representation (polynomial basis like PAR2, or
/// Leopard's Cantor basis) can use the same SIMD kernels.
/// </summary>
public interface IGf16Multiplier
{
    ushort Mul(ushort a, ushort b);
}

/// <summary>Multiplication in some representation of GF(2^8).</summary>
public interface IGf8Multiplier
{
    byte Mul(byte a, byte b);
}

using DeveParTools.Core.Hashing;

namespace DeveParTools.Par2.Tests.TestSupport;

/// <summary>
/// Makes data with a chosen CRC-32, to test what happens when the weak checksum matches but MD5 does not.
/// </summary>
/// <remarks>
/// For a fixed length, CRC-32 is affine over GF(2): <c>crc(m ⊕ d) = crc(m) ⊕ crc(d) ⊕ crc(0)</c>. Flipping bit k of a
/// 4-byte patch changes the CRC by a fixed vector v_k, so the patch x with <c>Σ x_k·v_k = target ⊕ crc(base)</c>
/// is found by Gaussian elimination on 32 equations in 32 unknowns (solvable for any 32 consecutive bits).
/// </remarks>
internal static class CrcForge
{
    /// <summary>Overwrites <paramref name="data"/>[pos..pos+4] so that the CRC-32 of the whole array is <paramref name="target"/>.</summary>
    public static void Force(byte[] data, int pos, uint target)
    {
        Array.Clear(data, pos, 4);
        uint crc0 = Crc32Ieee.Compute(data);
        var effect = new uint[32];
        for (int k = 0; k < 32; k++)
        {
            data[pos + k / 8] ^= (byte)(1 << (k % 8));
            effect[k] = Crc32Ieee.Compute(data) ^ crc0;
            data[pos + k / 8] ^= (byte)(1 << (k % 8));
        }

        // Row b: which patch bits change CRC bit b (bits 0..31), and the wanted change of bit b (bit 32).
        uint rhs = target ^ crc0;
        var rows = new ulong[32];
        for (int b = 0; b < 32; b++)
        {
            for (int k = 0; k < 32; k++)
            {
                rows[b] |= (ulong)((effect[k] >> b) & 1) << k;
            }

            rows[b] |= (ulong)((rhs >> b) & 1) << 32;
        }

        for (int k = 0; k < 32; k++)
        {
            int pivot = Array.FindIndex(rows, k, r => ((r >> k) & 1) != 0);
            (rows[k], rows[pivot]) = (rows[pivot], rows[k]);
            for (int r = 0; r < 32; r++)
            {
                if (r != k && ((rows[r] >> k) & 1) != 0)
                {
                    rows[r] ^= rows[k];
                }
            }
        }

        for (int k = 0; k < 32; k++)
        {
            if (((rows[k] >> 32) & 1) != 0)
            {
                data[pos + k / 8] ^= (byte)(1 << (k % 8));
            }
        }
    }
}

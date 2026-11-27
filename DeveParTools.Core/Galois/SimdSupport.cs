using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DeveParTools.Core.Galois;

/// <summary>Instruction-set paths available for the Galois field kernels.</summary>
public enum SimdLevel
{
    Scalar = 0,
    Ssse3 = 1,
    Avx2 = 2,
    Avx512 = 3,
    Gfni128 = 4,
    Gfni256 = 5,
    Gfni512 = 6,
    Neon = 7,
}

public static class SimdSupport
{
    private static readonly SimdLevel[] Preference =
    [
        SimdLevel.Gfni512,
        SimdLevel.Avx512,
        SimdLevel.Gfni256,
        SimdLevel.Avx2,
        SimdLevel.Gfni128,
        SimdLevel.Ssse3,
        SimdLevel.Neon,
        SimdLevel.Scalar,
    ];

    public static bool IsSupported(SimdLevel level) => level switch
    {
        SimdLevel.Scalar => true,
        SimdLevel.Ssse3 => Ssse3.IsSupported,
        SimdLevel.Avx2 => Avx2.IsSupported,
        SimdLevel.Avx512 => Avx512BW.IsSupported,
        SimdLevel.Gfni128 => Gfni.IsSupported && Sse41.IsSupported,
        SimdLevel.Gfni256 => Gfni.V256.IsSupported && Avx2.IsSupported,
        SimdLevel.Gfni512 => Gfni.V512.IsSupported && Avx512BW.IsSupported,
        SimdLevel.Neon => AdvSimd.Arm64.IsSupported,
        _ => false,
    };

    /// <summary>The fastest supported level on this machine.</summary>
    public static SimdLevel Best { get; } = Preference.First(IsSupported);

    /// <summary>All supported levels, fastest first.</summary>
    public static IReadOnlyList<SimdLevel> SupportedLevels { get; } = Preference.Where(IsSupported).ToArray();

    /// <summary>A short human-readable description of the CPU features relevant to PAR processing.</summary>
    public static string Describe()
    {
        var features = new List<string>();
        if (Sse2.IsSupported) features.Add("SSE2");
        if (Ssse3.IsSupported) features.Add("SSSE3");
        if (Pclmulqdq.IsSupported) features.Add("PCLMUL");
        if (Avx2.IsSupported) features.Add("AVX2");
        if (Avx512F.IsSupported) features.Add("AVX-512F");
        if (Avx512BW.IsSupported) features.Add("AVX-512BW");
        if (Gfni.IsSupported) features.Add("GFNI");
        if (AdvSimd.IsSupported) features.Add("NEON");
        if (AdvSimd.Arm64.IsSupported) features.Add("ARM64");
        return features.Count == 0 ? "none" : string.Join(", ", features);
    }
}

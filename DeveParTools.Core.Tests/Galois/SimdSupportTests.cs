using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using DeveParTools.Core.Galois;

namespace DeveParTools.Core.Tests.Galois;

[TestClass]
public sealed class SimdSupportTests
{
    [TestMethod]
    public void SupportedLevels_AreExactlyTheSupportedOnes_FastestFirst()
    {
        var all = Enum.GetValues<SimdLevel>();
        CollectionAssert.AreEquivalent(all.Where(SimdSupport.IsSupported).ToArray(), SimdSupport.SupportedLevels.ToArray());
        Assert.AreEqual(SimdSupport.SupportedLevels[0], SimdSupport.Best);
        Assert.AreEqual(SimdLevel.Scalar, SimdSupport.SupportedLevels[^1], "scalar is always supported and always the slowest");
        Assert.IsTrue(SimdSupport.IsSupported(SimdLevel.Scalar));
    }

    [TestMethod]
    public void IsSupported_FollowsTheInstructionSets()
    {
        Assert.AreEqual(Ssse3.IsSupported, SimdSupport.IsSupported(SimdLevel.Ssse3));
        Assert.AreEqual(Avx2.IsSupported, SimdSupport.IsSupported(SimdLevel.Avx2));
        Assert.AreEqual(Avx512BW.IsSupported, SimdSupport.IsSupported(SimdLevel.Avx512));
        Assert.AreEqual(Gfni.IsSupported && Sse41.IsSupported, SimdSupport.IsSupported(SimdLevel.Gfni128));
        Assert.AreEqual(AdvSimd.Arm64.IsSupported, SimdSupport.IsSupported(SimdLevel.Neon));

        // A GFNI or AVX-512 level implies the narrower ones of the same family.
        if (SimdSupport.IsSupported(SimdLevel.Gfni512))
        {
            Assert.IsTrue(SimdSupport.IsSupported(SimdLevel.Avx512));
        }

        if (SimdSupport.IsSupported(SimdLevel.Gfni256))
        {
            Assert.IsTrue(SimdSupport.IsSupported(SimdLevel.Avx2));
        }
    }

    [TestMethod]
    public void IsSupported_UnknownLevelIsNotSupported() => Assert.IsFalse(SimdSupport.IsSupported((SimdLevel)99));

    [TestMethod]
    public void Describe_ListsTheCpuFeatures()
    {
        var expected = new List<string>();
        void Add(bool supported, string name)
        {
            if (supported)
            {
                expected.Add(name);
            }
        }

        Add(Sse2.IsSupported, "SSE2");
        Add(Ssse3.IsSupported, "SSSE3");
        Add(Pclmulqdq.IsSupported, "PCLMUL");
        Add(Avx2.IsSupported, "AVX2");
        Add(Avx512F.IsSupported, "AVX-512F");
        Add(Avx512BW.IsSupported, "AVX-512BW");
        Add(Gfni.IsSupported, "GFNI");
        Add(AdvSimd.IsSupported, "NEON");
        Add(AdvSimd.Arm64.IsSupported, "ARM64");
        Assert.AreEqual(expected.Count == 0 ? "none" : string.Join(", ", expected), SimdSupport.Describe());
    }
}

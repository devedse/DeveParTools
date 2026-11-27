using DeveParTools.Core.Hashing;

namespace DeveParTools.Core.Tests.Hashing;

[TestClass]
public sealed class Blake3Tests
{
    // Official BLAKE3 test vectors (input byte i = i % 251), first 32 bytes of the hash.
    [TestMethod]
        [DataRow(0, "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")]
        [DataRow(1, "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213")]
        [DataRow(2, "7b7015bb92cf0b318037702a6cdd81dee41224f734684c2c122cd6359cb1ee63")]
        [DataRow(3, "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f")]
        [DataRow(4, "f30f5ab28fe047904037f77b6da4fea1e27241c5d132638d8bedce9d40494f32")]
        [DataRow(5, "b40b44dfd97e7a84a996a91af8b85188c66c126940ba7aad2e7ae6b385402aa2")]
        [DataRow(6, "06c4e8ffb6872fad96f9aaca5eee1553eb62aed0ad7198cef42e87f6a616c844")]
        [DataRow(7, "3f8770f387faad08faa9d8414e9f449ac68e6ff0417f673f602a646a891419fe")]
        [DataRow(8, "2351207d04fc16ade43ccab08600939c7c1fa70a5c0aaca76063d04c3228eaeb")]
        [DataRow(63, "e9bc37a594daad83be9470df7f7b3798297c3d834ce80ba85d6e207627b7db7b")]
        [DataRow(64, "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f4c6dc7f2511b98")]
        [DataRow(65, "de1e5fa0be70df6d2be8fffd0e99ceaa8eb6e8c93a63f2d8d1c30ecb6b263dee")]
        [DataRow(127, "d81293fda863f008c09e92fc382a81f5a0b4a1251cba1634016a0f86a6bd640d")]
        [DataRow(128, "f17e570564b26578c33bb7f44643f539624b05df1a76c81f30acd548c44b45ef")]
        [DataRow(129, "683aaae9f3c5ba37eaaf072aed0f9e30bac0865137bae68b1fde4ca2aebdcb12")]
        [DataRow(1023, "10108970eeda3eb932baac1428c7a2163b0e924c9a9e25b35bba72b28f70bd11")]
        [DataRow(1024, "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7")]
        [DataRow(1025, "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444")]
        [DataRow(2048, "e776b6028c7cd22a4d0ba182a8bf62205d2ef576467e838ed6f2529b85fba24a")]
        [DataRow(2049, "5f4d72f40d7a5f82b15ca2b2e44b1de3c2ef86c426c95c1af0b6879522563030")]
        [DataRow(3072, "b98cb0ff3623be03326b373de6b9095218513e64f1ee2edd2525c7ad1e5cffd2")]
        [DataRow(3073, "7124b49501012f81cc7f11ca069ec9226cecb8a2c850cfe644e327d22d3e1cd3")]
        [DataRow(4096, "015094013f57a5277b59d8475c0501042c0b642e531b0a1c8f58d2163229e969")]
        [DataRow(4097, "9b4052b38f1c5fc8b1f9ff7ac7b27cd242487b3d890d15c96a1c25b8aa0fb995")]
        [DataRow(5120, "9cadc15fed8b5d854562b26a9536d9707cadeda9b143978f319ab34230535833")]
        [DataRow(5121, "628bd2cb2004694adaab7bbd778a25df25c47b9d4155a55f8fbd79f2fe154cff")]
        [DataRow(6144, "3e2e5b74e048f3add6d21faab3f83aa44d3b2278afb83b80b3c35164ebeca205")]
        [DataRow(6145, "f1323a8631446cc50536a9f705ee5cb619424d46887f3c376c695b70e0f0507f")]
        [DataRow(7168, "61da957ec2499a95d6b8023e2b0e604ec7f6b50e80a9678b89d2628e99ada77a")]
        [DataRow(7169, "a003fc7a51754a9b3c7fae0367ab3d782dccf28855a03d435f8cfe74605e7817")]
        [DataRow(8192, "aae792484c8efe4f19e2ca7d371d8c467ffb10748d8a5a1ae579948f718a2a63")]
        [DataRow(8193, "bab6c09cb8ce8cf459261398d2e7aef35700bf488116ceb94a36d0f5f1b7bc3b")]
        [DataRow(16384, "f875d6646de28985646f34ee13be9a576fd515f76b5b0a26bb324735041ddde4")]
        [DataRow(31744, "62b6960e1a44bcc1eb1a611a8d6235b6b4b78f32e7abc4fb4c6cdcce94895c47")]
        [DataRow(102400, "bc3e3d41a1146b069abffad3c0d44860cf664390afce4d9661f7902e7943e085")]
    public void OfficialVectors(int length, string expectedHex)
    {
        var input = MakeInput(length);
        Assert.AreEqual(expectedHex, Convert.ToHexStringLower(Blake3.Hash(input)), "one-shot");

        var h = new Blake3Hasher();
        h.Update(input);
        Assert.AreEqual(expectedHex, Convert.ToHexStringLower(h.Finalize()), "streaming single update");

        foreach (int piece in new[] { 1, 63, 64, 1000, 1024, 1025, 5000 })
        {
            var hp = new Blake3Hasher();
            for (int pos = 0; pos < length; pos += piece)
            {
                hp.Update(input.AsSpan(pos, Math.Min(piece, length - pos)));
            }

            Assert.AreEqual(expectedHex, Convert.ToHexStringLower(hp.Finalize()), $"streaming piece={piece}");
        }
    }

    [TestMethod]
    public void LargeInput_OneShotParallelMatchesStreaming()
    {
        var input = MakeInput(10 * 1024 * 1024 + 12345);
        var oneShot = Blake3.Hash(input);
        var h = new Blake3Hasher();
        for (int pos = 0; pos < input.Length; pos += 777_777)
        {
            h.Update(input.AsSpan(pos, Math.Min(777_777, input.Length - pos)));
        }

        CollectionAssert.AreEqual(oneShot, h.Finalize());
    }

    [TestMethod]
    public void TruncatedOutputIsPrefix()
    {
        var input = MakeInput(3000);
        var full = Blake3.Hash(input);
        var sixteen = new byte[16];
        Blake3.Hash(input, sixteen);
        CollectionAssert.AreEqual(full.AsSpan(0, 16).ToArray(), sixteen);
    }

    private static byte[] MakeInput(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)(i % 251);
        }

        return b;
    }
}

using System.Text;

namespace CX.Framework;

/// <summary>The managed SHA-256 against the FIPS 180-2 Appendix B digests, and against <c>sha256sum</c> on runs of
/// 'a' that land on each side of the padding's 56- and 64-byte boundaries.</summary>
internal static class Sha256SelfCheck
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Sha256.SelfCheck");
        void Digest(string text, string want, string what)
        {
            string got = Convert.ToHexStringLower(Sha256.HashData(Encoding.ASCII.GetBytes(text)));
            check.Expect(got == want, $"{what} ({got})");
        }
        Digest("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "the empty message");
        Digest("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "FIPS 180-2 one-block message");
        Digest("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
               "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", "FIPS 180-2 two-block message");
        Digest(new string('a', 55), "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318", "55 bytes, the length fits the block");
        Digest(new string('a', 56), "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a", "56 bytes, the length spills a block");
        Digest(new string('a', 63), "7d3e74a05d7db15bce4ad9ec0658ea98e3f06eeecf16b4c6fff2da457ddc2f34", "63 bytes");
        Digest(new string('a', 64), "ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb", "64 bytes, one whole block");
        Digest(new string('a', 119), "31eba51c313a5c08226adf18d4a359cfdfd8d2e816b13f4af952f7ea6584dcfb", "119 bytes");
        Digest(new string('a', 120), "2f3d335432c70b580af0e8e1b3674a7c020d683aa5f73aaaedfdc55af904c21c", "120 bytes");
        log.Info(check.Ok ? "Sha256.SelfCheck: passed" : "Sha256.SelfCheck: FAILED");
        return check.Ok;
    }
}

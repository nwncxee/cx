using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>A replacement TGA in the development folder is not served the stock DDS's cached build (approved example E1).
/// Oracle: hand-written sources for one resref, a stock DDS in a base BIF and a TGA in the development folder.</summary>
internal static unsafe partial class OwnedTextures
{
    private static TexSource _fakeSource;
    private static TexSource FakeSource(byte* name) => _fakeSource;

    internal static void SelfCheckDevTga(ExtenderLog log)
    {
        var check = new Checker(log, "OwnedTextures.DevTgaApprovedExamples.SelfCheck");
        var svMeta = new System.Collections.Generic.Dictionary<ulong, TexMeta>(_meta);
        delegate*<byte*, TexSource> svSource = _sourceOf;
        _sourceOf = &FakeSource;
        byte* name = stackalloc byte[] { (byte)'i', (byte)'w', (byte)'a', (byte)'x', (byte)'b', (byte)'t', (byte)'_', (byte)'t', (byte)'_', (byte)'0', (byte)'1', (byte)'1', 0 };
        const ulong k0 = 0xD0D0000UL;
        var stockDds = new TexSource(0x7f1, 1, 4, 9, 117);
        var developmentTga = new TexSource(3, 0, 0, 12, 3);
        try
        {
            _meta.Clear();
            // Given the client already built a texture from a stock DDS in a base BIF
            _meta[k0] = new TexMeta { W = 64, H = 128, Bpp = 4, NumX = 1, NumY = 1, Source = stockDds };
            _fakeSource = stockDds;
            check.Expect(WarmMeta(k0, name, out _), "a texture whose name still resolves to the DDS it was built from is served warm");
            // When a TGA of the same name lands in development/ and the engine builds that texture again
            _fakeSource = developmentTga;
            bool warm = WarmMeta(k0, name, out _);
            // Then the build uses the TGA's pixels, not the cached DDS build
            check.Expect(!warm, "a texture whose name now resolves to the development folder's TGA is not served the DDS build");
            check.Expect(!_meta.ContainsKey(k0), "the DDS build's record is dropped, so the cold build records the TGA");
        }
        finally
        {
            _sourceOf = svSource;
            _meta.Clear(); foreach (var kv in svMeta) _meta[kv.Key] = kv.Value;
        }
        log.Info(check.Ok ? "OwnedTextures.DevTgaApprovedExamples.SelfCheck: passed" : "OwnedTextures.DevTgaApprovedExamples.SelfCheck: FAILED");
    }
}

using CX.Framework;

namespace CX.Services.Nui;

/// <summary>A part icon shipped as a DDS with a 1x1 TGA stub of its name composites from the DDS (approved example E4).
/// Oracle: a hand-built BioWare DDS, 4x4 with one DXT5 block of opaque pure red, and the restype ids 0x7f1 DDS, 3 TGA.</summary>
internal static unsafe partial class NuiItemIcons
{
    internal static void SelfCheckDevTga(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemIcons.DevTgaApprovedExamples.SelfCheck");
        byte[] dds =
        [
            4, 0, 0, 0, 4, 0, 0, 0, 4, 0, 0, 0, 16, 0, 0, 0, 0, 0, 128, 63,
            255, 255, 0, 0, 0, 0, 0, 0, 0, 248, 0, 248, 0, 0, 0, 0,
        ];
        // Given a part icon shipped as a DDS with a 1x1 TGA stub of the same name
        bool fromDds = LayerFromDds(0x7f1) && !LayerFromDds(3);   // not-an-offset: the DDS restype id
        // When an @look: or @item: NUI icon composites its layers
        uint[]? px = DdsLayer(dds, out int w, out int h);
        // Then it draws the DDS's pixels, not the stub
        check.Expect(fromDds, "a layer whose name the race gives to a DDS is read from the DDS, and one it gives to a TGA is not");
        check.Expect(px != null && w == 4 && h == 4, $"the DDS layer decodes at its own size ({w}x{h})");
        bool red = px != null && px.Length == 16;
        for (int i = 0; red && i < 16; i++) red = px![i] == 0xFF0000FFu;   // not-an-offset: opaque red, R in the low byte
        check.Expect(red, "every texel of the DDS layer is the block's opaque red");
        log.Info(check.Ok ? "NuiItemIcons.DevTgaApprovedExamples.SelfCheck: passed" : "NuiItemIcons.DevTgaApprovedExamples.SelfCheck: FAILED");
    }
}

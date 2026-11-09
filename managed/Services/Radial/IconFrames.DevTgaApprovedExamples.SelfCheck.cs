using CX.Alloy;
using CX.Framework;

namespace CX.Services.Radial;

/// <summary>An icon's frame is measured on the file the engine draws (approved example E2). Oracle: hand-written container
/// kinds (1 base BIF, 2 directory, as <c>CExoKeyTable::BuildNewTable</c> sets them) and restype ids (3 TGA, 0x7f1 DDS).</summary>
internal static partial class IconFrames
{
    internal static void SelfCheckDevTga(ExtenderLog log)
    {
        var check = new Checker(log, "IconFrames.DevTgaApprovedExamples.SelfCheck");
        // Given a stock DDS icon in a base BIF and a replacement TGA of the same name in development/
        int won = TexLoad.Pick([0, 1, 0, 2]);   // found as KTX, DDS, 4PC, TGA
        // When an @item: NUI icon or the radial measures the icon's frame
        System.ReadOnlySpan<ushort> order = ReadOrder(3);
        // Then it measures the TGA
        check.Expect(won == 3, $"the race gives a development folder TGA over a base BIF DDS (format {won})");
        check.Expect(order.Length > 0 && order[0] == 3, $"a frame whose race went to the TGA reads the TGA first (restype {(order.Length > 0 ? order[0] : 0)})");
        check.Expect(ReadOrder(0x7f1)[0] == 0x7f1 && ReadOrder(0)[0] == 0x7f1, "a DDS winner, or no answer, still reads the DDS first");   // not-an-offset: the DDS restype id
        log.Info(check.Ok ? "IconFrames.DevTgaApprovedExamples.SelfCheck: passed" : "IconFrames.DevTgaApprovedExamples.SelfCheck: FAILED");
    }
}

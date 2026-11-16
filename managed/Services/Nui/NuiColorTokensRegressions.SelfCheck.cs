using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>Edges of NUI colour tokens no approved example names: a token missing its closing bracket, colours nested
/// past the native stack, and a draw with more tokens than pieces. Oracle: the texts, piece counts and colours typed here.</summary>
internal static unsafe class NuiColorTokensRegressions
{
    /// <summary>The label's own colour as an nk_color word.</summary>
    private const uint Own = 0xFFC8C8C8; // not-an-offset: an nk_color

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiColorTokensRegressions.SelfCheck");
        bool nui = NuiKnobs.Enabled.V, on = NuiKnobs.ColorTokens.V;
        NuiKnobs.Enabled.Set(true);
        NuiKnobs.ColorTokens.Set(true);
        Span<ColorPiece> pieces = stackalloc ColorPiece[NuiColorText.MaxPieces];

        byte[] open = Encoding.UTF8.GetBytes("<cab x and <cabcd");
        check.Expect(NuiColorText.Plan(open, Own, Font, pieces) == -1, "a <c with no > after its three bytes is drawn as text");
        check.Expect(NuiColorText.Width(open, Font) == open.Length * 10f, "a <c with no > after its three bytes measures as text");

        var deep = new StringBuilder();
        for (int k = 1; k <= 17; k++) deep.Append("<c").Append((char)k).Append((char)k).Append((char)k).Append('>').Append('x');
        byte[] deepBytes = Encoding.UTF8.GetBytes(deep.ToString());
        int n = NuiColorText.Plan(deepBytes, Own, Font, pieces);
        check.Expect(n == 17, $"17 nested opens lay out as 17 pieces, got {n}");
        if (n == 17)
            check.Expect(pieces[16].Color == 0xFF101010u, // not-an-offset: an nk_color
                $"the 17th open, past the 16 the native stack holds, keeps the 16th colour: got 0x{pieces[16].Color:X8}");

        var many = new StringBuilder();
        for (int k = 0; k < 70; k++) many.Append("<c\u0001\u0001\u0001>y</c>");
        byte[] manyBytes = Encoding.UTF8.GetBytes(many.ToString());
        n = NuiColorText.Plan(manyBytes, Own, Font, pieces);
        check.Expect(n == NuiColorText.MaxPieces, $"a draw with 70 tokened runs fills every piece, got {n}");
        if (n == NuiColorText.MaxPieces)
        {
            var last = pieces[n - 1];
            check.Expect(last.Start + last.Length == manyBytes.Length, "the last piece runs to the end of the text, so none of it is lost");
        }

        NuiKnobs.ColorTokens.Set(on);
        NuiKnobs.Enabled.Set(nui);
        log.Info(check.Ok ? "NuiColorTokensRegressions.SelfCheck: passed" : "NuiColorTokensRegressions.SelfCheck: FAILED");
    }

    /// <summary>The stand-in font: ten pixels per byte.</summary>
    private static TextMeasure Font => new(IntPtr.Zero, 12f, &TenPerByte);

    [UnmanagedCallersOnly]
    private static float TenPerByte(IntPtr userData, float height, byte* text, int length) => length * 10f;
}

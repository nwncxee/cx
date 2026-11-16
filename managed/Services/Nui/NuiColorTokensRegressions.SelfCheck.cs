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

        byte[] zero = Encoding.UTF8.GetBytes("<c\u0000ab>z");
        check.Expect(NuiColorText.Plan(zero, Own, Font, pieces) == -1, "a <c whose colour holds a zero byte is drawn as text, as the native reader refuses it");

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

        Stretches(check, pieces);

        NuiKnobs.ColorTokens.Set(on);
        NuiKnobs.Enabled.Set(nui);
        log.Info(check.Ok ? "NuiColorTokensRegressions.SelfCheck: passed" : "NuiColorTokensRegressions.SelfCheck: FAILED");
    }

    /// <summary>A frame's queued commands, by buffer offset: an edit field at 100..200 and a text block at 300..400. A
    /// command in the edit field draws as typed, the block's lines carry their colour, and the next frame forgets both.</summary>
    private static void Stretches(Checker check, Span<ColorPiece> pieces)
    {
        byte[] red = Encoding.UTF8.GetBytes("<c\u00fe\u0001\u0001>Hi");
        byte[] plain = Encoding.UTF8.GetBytes("more");
        NuiColorText.EndFrame();
        NuiColorText.Mark(100, 200, edit: true);
        NuiColorText.Mark(300, 400, edit: false);
        NuiColorText.Mark(50, 60, edit: true);
        check.Expect(NuiColorText.PlanAt(150, red, Own, Font, pieces) == -1, "a command inside the edit field's stretch draws as typed");
        check.Expect(NuiColorText.PlanAt(100, red, Own, Font, pieces) == -1, "a command at the edit field's first byte draws as typed");
        check.Expect(NuiColorText.PlanAt(200, red, Own, Font, pieces) == 1, "a command at the edit field's end is past it, and coloured");
        check.Expect(NuiColorText.PlanAt(55, red, Own, Font, pieces) == 1, "a stretch marked below one already held is refused, so its command is coloured");
        check.Expect(NuiColorText.PlanAt(250, red, Own, Font, pieces) == 1 && pieces[0].Color == 0xFF0101FEu, // not-an-offset: an nk_color
            "a command between the stretches is coloured");
        check.Expect(NuiColorText.PlanAt(260, plain, Own, Font, pieces) == -1, "an unclosed colour outside any block stops at its own command");
        NuiColorText.PlanAt(310, red, Own, Font, pieces);
        int n = NuiColorText.PlanAt(350, plain, Own, Font, pieces);
        check.Expect(n == 1 && pieces[0].Color == 0xFF0101FEu, "the next line of the same block carries the open colour"); // not-an-offset: an nk_color
        check.Expect(NuiColorText.PlanAt(450, plain, Own, Font, pieces) == -1, "a command after the block does not carry its colour");
        NuiColorText.EndFrame();
        check.Expect(NuiColorText.PlanAt(150, red, Own, Font, pieces) == 1, "the next frame forgets the edit field's stretch");
        NuiColorText.EndFrame();
    }

    /// <summary>The stand-in font: ten pixels per byte.</summary>
    private static TextMeasure Font => new(IntPtr.Zero, 12f, &TenPerByte);

    [UnmanagedCallersOnly]
    private static float TenPerByte(IntPtr userData, float height, byte* text, int length) => length * 10f;
}

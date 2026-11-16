using System;
using System.Text;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>Edges of NUI colour tokens no approved example names: malformed and cut-off tokens, nesting past the native
/// stack, more tokens than pieces, and the edit-field and text-block stretches of a frame, full ones included. Oracle:
/// the texts, piece counts and colours typed here.</summary>
internal static unsafe class NuiColorTokensRegressions
{
    private const uint Own = NuiColorTokensApprovedExamples.Own;
    private const uint Red = NuiColorTokensApprovedExamples.Red;
    /// <summary>(16, 16, 16), the 16th nested open, at the label's alpha.</summary>
    private const uint Sixteenth = 0xFF101010; // not-an-offset: an nk_color

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

        check.Expect(NuiColorText.Width(Encoding.UTF8.GetBytes("a <c>"), Font) == 50f, "a text ending in <c> with no colour between measures all of it");
        check.Expect(NuiColorText.Width(Encoding.UTF8.GetBytes("when x <"), Font) == 80f, "a text ending in a lone < measures all of it");

        var deep = new StringBuilder();
        for (int k = 1; k <= 17; k++) deep.Append("<c").Append((char)k).Append((char)k).Append((char)k).Append('>').Append('x');
        byte[] deepBytes = Encoding.UTF8.GetBytes(deep.ToString());
        int n = NuiColorText.Plan(deepBytes, Own, Font, pieces);
        check.Expect(n == 17, $"17 nested opens lay out as 17 pieces, got {n}");
        if (n == 17)
            check.Expect(pieces[16].Color == Sixteenth,
                $"the 17th open, past the 16 the native stack holds, keeps the 16th colour: got 0x{pieces[16].Color:X8}");

        var many = new StringBuilder();
        for (int k = 0; k < 70; k++) many.Append("<c\u0001\u0001\u0001>y</c>");
        byte[] manyBytes = Encoding.UTF8.GetBytes(many.ToString());
        n = NuiColorText.Plan(manyBytes, Own, Font, pieces);
        check.Expect(n == 64, $"a draw with 70 tokened runs fills all 64 pieces, got {n}");
        if (n == 64)
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
        byte[] red = Encoding.UTF8.GetBytes(NuiColorTokensApprovedExamples.Tok(254, 1, 1) + "Hi");
        byte[] plain = Encoding.UTF8.GetBytes("more");
        NuiColorText.EndFrame();
        NuiColorText.Mark(100, 200, edit: true);
        NuiColorText.Mark(300, 400, edit: false);
        NuiColorText.Mark(350, 450, edit: true);
        check.Expect(NuiColorText.PlanAt(150, red, Own, Font, pieces) == -1, "a command inside the edit field's stretch draws as typed");
        check.Expect(NuiColorText.PlanAt(100, red, Own, Font, pieces) == -1, "a command at the edit field's first byte draws as typed");
        check.Expect(NuiColorText.PlanAt(200, red, Own, Font, pieces) == 1, "a command at the edit field's end is past it, and coloured");
        check.Expect(NuiColorText.PlanAt(420, red, Own, Font, pieces) == 1, "a stretch overlapping one already held is refused, so its command is coloured");
        check.Expect(NuiColorText.PlanAt(250, red, Own, Font, pieces) == 1 && pieces[0].Color == Red,
            "a command between the stretches is coloured");
        check.Expect(NuiColorText.PlanAt(260, plain, Own, Font, pieces) == -1, "an unclosed colour outside any block stops at its own command");
        NuiColorText.PlanAt(310, red, Own, Font, pieces);
        int n = NuiColorText.PlanAt(350, plain, Own, Font, pieces);
        check.Expect(n == 1 && pieces[0].Color == Red, "the next line of the same block carries the open colour");
        check.Expect(NuiColorText.PlanAt(450, plain, Own, Font, pieces) == -1, "a command after the block does not carry its colour");
        NuiColorText.EndFrame();
        check.Expect(NuiColorText.PlanAt(150, red, Own, Font, pieces) == 1, "the next frame forgets the edit field's stretch");
        NuiColorText.EndFrame();

        for (ulong k = 0; k < 1025; k++) NuiColorText.Mark(k * 10, k * 10 + 5, edit: false);
        check.Expect(NuiColorText.PlanAt(10235, red, Own, Font, pieces) == -1,
            "past a full frame's last stretch text draws as the stock client draws it, since an edit field there went unmarked");
        check.Expect(NuiColorText.PlanAt(10225, red, Own, Font, pieces) == 1, "text before a full frame's last stretch is still coloured");
        NuiColorText.EndFrame();
        check.Expect(NuiColorText.PlanAt(10235, red, Own, Font, pieces) == 1, "the next frame starts with room again");
        NuiColorText.EndFrame();
    }

    private static TextMeasure Font => NuiColorTokensApprovedExamples.Font;
}

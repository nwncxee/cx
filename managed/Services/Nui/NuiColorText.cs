using System;

namespace CX.Services.Nui;

/// <summary>One stretch of a NUI text draw in one colour: its bytes in the drawn string, its x from the draw's
/// left edge, and its <c>nk_color</c> packed as nuklear passes it by value (r in the low byte, a in the high).</summary>
internal readonly record struct ColorPiece(int Start, int Length, float X, uint Color);

/// <summary>A font's width of a byte run, as nuklear's <c>nk_text_width_f</c> asks it: the font's userdata, its
/// height, the UTF-8 bytes and their count.</summary>
internal readonly unsafe struct TextMeasure(IntPtr userData, float height, delegate* unmanaged<IntPtr, float, byte*, int, float> width)
{
    public readonly IntPtr UserData = userData;
    public readonly float Height = height;
    public readonly delegate* unmanaged<IntPtr, float, byte*, int, float> Width = width;
}

/// <summary>NWN's inline colour tokens in NUI text, laid out the way the client's own text code reads them:
/// which bytes of a draw show in which colour, and how wide the shown text is.</summary>
internal static unsafe class NuiColorText
{
    /// <summary>Pieces one draw can split into; a draw with more keeps its later tokens as text.</summary>
    internal const int MaxPieces = 64;

    /// <summary>The colour pieces of one text draw in <paramref name="fg"/>, written into <paramref name="into"/>;
    /// -1 when the draw goes ahead unchanged.</summary>
    internal static int Plan(ReadOnlySpan<byte> utf8, uint fg, TextMeasure measure, Span<ColorPiece> into) => -1;

    /// <summary>The width of the text a draw of <paramref name="utf8"/> shows.</summary>
    internal static float Width(ReadOnlySpan<byte> utf8, TextMeasure measure)
    {
        fixed (byte* p = utf8) return measure.Width(measure.UserData, measure.Height, p, utf8.Length);
    }

    /// <summary>A text block's lines draw between these two: a colour open at a line's end carries to the next.</summary>
    internal static void BeginBlock() { }

    /// <inheritdoc cref="BeginBlock"/>
    internal static void EndBlock() { }

    /// <summary>An edit field draws between these two: its text shows and measures as typed.</summary>
    internal static void EnterEdit() { }

    /// <inheritdoc cref="EnterEdit"/>
    internal static void LeaveEdit() { }
}

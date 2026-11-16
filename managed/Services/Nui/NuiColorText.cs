using System;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;

namespace CX.Services.Nui;

/// <summary>One stretch of a NUI text draw in one colour: its bytes in the drawn string, its x from the draw's
/// left edge, and its <c>nk_color</c> packed as nuklear passes it by value (r in the low byte, a in the high).</summary>
internal readonly record struct ColorPiece(int Start, int Length, float X, uint Color);

/// <summary>A font's width of a byte run, as nuklear's <c>nk_text_width_f</c> asks it: the font's userdata, its
/// height, the UTF-8 bytes and their count.</summary>
internal readonly unsafe struct TextMeasure(IntPtr userData, float height, NkFontTextWidthFn width)
{
    /// <summary>The width of <paramref name="run"/> in this font.</summary>
    public float Of(ReadOnlySpan<byte> run)
    {
        fixed (byte* p = run) return width(userData, height, p, run.Length);
    }
}

/// <summary>NWN's inline colour tokens in NUI text, laid out the way the client's own text code reads them:
/// which bytes of a draw show in which colour, and how wide the shown text is.</summary>
internal static unsafe class NuiColorText
{
    /// <summary>Pieces one draw can split into; a draw with more keeps its later tokens as text.</summary>
    internal const int MaxPieces = 64;

    /// <summary>Edit fields and text blocks one frame can mark. ponytail: past this the frame's later text draws as
    /// the stock client draws it, tokens and all; raise it if a window ever needs more.</summary>
    internal const int MaxStretches = 1024;

    /// <summary>The alpha byte of an <c>nk_color</c>: a token's colour keeps the element's own.</summary>
    private const uint Alpha = 0xFF000000u; // not-an-offset: a colour mask

    /// <summary>A stretch of nuklear's command memory one edit field or one text block pushed this frame.</summary>
    internal readonly record struct Stretch(ulong From, ulong To, bool Edit);

    /// <summary>One frame's layout state: the colour stack a text block carries from line to line, whether an edit
    /// field is drawing, the stretches the frame marked, and the codepage map read this frame.</summary>
    internal sealed class ColorTextState
    {
        public readonly uint[] Stack = new uint[ColorRunParser.MaxDepth];
        public int Depth;
        public bool InBlock;
        public int Edit;
        /// <summary>The codepoint each codepage byte 0x80..0xff stands for, read on the frame's first token that needs it.</summary>
        public readonly int[] Codepoints = new int[128];
        public bool Mapped;
        public readonly Stretch[] Stretches = new Stretch[MaxStretches];
        public int StretchCount;
        /// <summary>True once the frame marked more stretches than it holds.</summary>
        public bool Full;
        /// <summary>The stretch the last laid-out command sat in, -1 for none.</summary>
        public int LastStretch = -1;
    }

    internal static ColorTextState State = new();

    private static bool On => NuiKnobs.Enabled.V && NuiKnobs.ColorTokens.V && State.Edit == 0;

    /// <summary>The colour pieces of one text draw in <paramref name="fg"/>, written into <paramref name="into"/>;
    /// -1 when the draw goes ahead unchanged.</summary>
    internal static int Plan(ReadOnlySpan<byte> utf8, uint fg, TextMeasure measure, Span<ColorPiece> into)
    {
        var s = State;
        if (!On) return -1;
        if (!s.InBlock) s.Depth = 0;
        if (utf8.IndexOf((byte)'<') < 0 && s.Depth == 0) return -1;

        int depthIn = s.Depth, n = 0, i = 0, len;
        bool tokens = false;
        float x = 0f;
        uint color = s.Depth > 0 ? s.Stack[s.Depth - 1] : fg;
        while (n < into.Length - 1 && (len = NextToken(utf8, i, out int at, out uint rgb, out bool open)) > 0)
        {
            tokens = true;
            if (at > i)
            {
                into[n++] = new ColorPiece(i, at - i, x, color);
                x += measure.Of(utf8[i..at]);
            }
            if (open) { if (s.Depth < s.Stack.Length) s.Stack[s.Depth++] = rgb | (fg & Alpha); }
            else if (s.Depth > 0) s.Depth--;
            color = s.Depth > 0 ? s.Stack[s.Depth - 1] : fg;
            i = at + len;
        }
        if (!tokens && depthIn == 0) return -1;
        if (i < utf8.Length) into[n++] = new ColorPiece(i, utf8.Length - i, x, color);
        return n;
    }

    /// <summary>The width of the text a draw of <paramref name="utf8"/> shows. A token the span cuts off takes no room
    /// either, since nuklear's clamp measures each glyph prefix.</summary>
    internal static float Width(ReadOnlySpan<byte> utf8, TextMeasure measure)
    {
        if (!On || utf8.IndexOf((byte)'<') < 0) return measure.Of(utf8);
        float w = 0f;
        int i = 0, len;
        while ((len = NextToken(utf8, i, out int at, out _, out _)) > 0)
        {
            if (at > i) w += measure.Of(utf8[i..at]);
            i = at + len;
        }
        int end = utf8.Length - CutToken(utf8[i..]);
        if (end > i) w += measure.Of(utf8[i..end]);
        return w;
    }

    /// <summary>A text block's lines draw between these two: a colour open at a line's end carries to the next.</summary>
    internal static void BeginBlock() { State.InBlock = true; State.Depth = 0; }

    /// <inheritdoc cref="BeginBlock"/>
    internal static void EndBlock() => State.InBlock = false;

    /// <summary>Notes that an edit field (<paramref name="edit"/>) or a text block pushed the commands between buffer
    /// offsets <paramref name="from"/> and <paramref name="to"/>. Commands are pushed in rising offsets, so the
    /// stretches stay sorted; an overlapping one is refused.</summary>
    internal static void Mark(ulong from, ulong to, bool edit)
    {
        var s = State;
        if (to <= from) return;
        if (s.StretchCount >= MaxStretches) { s.Full = true; return; }
        if (s.StretchCount > 0 && from < s.Stretches[s.StretchCount - 1].To) return;
        s.Stretches[s.StretchCount++] = new Stretch(from, to, edit);
    }

    /// <summary><see cref="Plan"/> for the queued text command at buffer offset <paramref name="at"/>: an edit field's
    /// text goes ahead as typed, the lines of one text block carry their colour from one to the next, and past a
    /// full frame's last stretch text goes ahead unchanged.</summary>
    internal static int PlanAt(ulong at, ReadOnlySpan<byte> utf8, uint fg, TextMeasure measure, Span<ColorPiece> into)
    {
        var s = State;
        if (s.Full && at >= s.Stretches[MaxStretches - 1].To) return -1;
        int k = Find(at);
        if (k >= 0 && s.Stretches[k].Edit) return -1;
        if (k < 0) EndBlock();
        else if (k != s.LastStretch || !s.InBlock) BeginBlock();
        s.LastStretch = k;
        return Plan(utf8, fg, measure, into);
    }

    /// <summary>The frame's commands are spent: forget its stretches, and read the codepage afresh next frame.</summary>
    internal static void EndFrame()
    {
        var s = State;
        s.StretchCount = 0;
        s.Full = false;
        s.LastStretch = -1;
        s.Mapped = false;
        EndBlock();
    }

    private static int Find(ulong at)
    {
        var s = State;
        int lo = 0, hi = s.StretchCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var r = s.Stretches[mid];
            if (at < r.From) hi = mid - 1;
            else if (at >= r.To) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>An edit field draws between these two: its text shows and measures as typed.</summary>
    internal static void EnterEdit() => State.Edit++;

    /// <inheritdoc cref="EnterEdit"/>
    internal static void LeaveEdit() => State.Edit--;

    /// <summary>The length of the first token in <paramref name="t"/> at or after <paramref name="from"/>, 0 for none;
    /// <paramref name="at"/> is where it starts. Not <see cref="ColorRunParser"/>: that reads Latin-1 strings and
    /// allocates, and this reads the UTF-8 nuklear draws, on the frame.</summary>
    private static int NextToken(ReadOnlySpan<byte> t, int from, out int at, out uint rgb, out bool open)
    {
        for (at = from; at < t.Length; at++)
        {
            int lt = t[at..].IndexOf((byte)'<');
            if (lt < 0) break;
            at += lt;
            int len = Token(t[at..], out rgb, out open);
            if (len > 0) return len;
        }
        at = t.Length;
        rgb = 0;
        open = false;
        return 0;
    }

    /// <summary>The length of the token <paramref name="t"/> starts with, 0 for none. An open token is <c>&lt;c</c>,
    /// three non-zero codepage bytes and <c>&gt;</c>, giving <paramref name="rgb"/>; a close token is <c>&lt;/c</c> and
    /// the one character after it, as the native reader takes four bytes.</summary>
    private static int Token(ReadOnlySpan<byte> t, out uint rgb, out bool open)
    {
        rgb = 0;
        open = false;
        if (t.Length < 3 || t[0] != (byte)'<') return 0;
        if (t[1] == (byte)'/')
        {
            if (t[2] != (byte)'c') return 0;
            if (t.Length == 3) return 3;
            Rune.DecodeFromUtf8(t[3..], out _, out int tail);
            return 3 + tail;
        }
        if (t[1] != (byte)'c') return 0;
        int at = 2;
        for (int k = 0; k < 3; k++)
        {
            if (at >= t.Length || Rune.DecodeFromUtf8(t[at..], out Rune r, out int len) != System.Buffers.OperationStatus.Done) return 0;
            int b = CodepageByte(r.Value);
            if (b <= 0) return 0;
            rgb |= (uint)b << (8 * k);
            at += len;
        }
        if (at >= t.Length || t[at] != (byte)'>') return 0;
        open = true;
        return at + 1;
    }

    /// <summary>The bytes at the end of <paramref name="t"/> that begin a token it cuts off: <c>&lt;c</c> and at most three
    /// characters, or <c>&lt;/</c>. A lone <c>&lt;</c> stays text.</summary>
    private static int CutToken(ReadOnlySpan<byte> t)
    {
        int lt = t.LastIndexOf((byte)'<');
        if (lt < 0 || t.Length - lt < 2) return 0;
        var rest = t[(lt + 2)..];
        if (t[lt + 1] == (byte)'/') return rest.IsEmpty ? t.Length - lt : 0;
        if (t[lt + 1] != (byte)'c' || rest.IndexOf((byte)'>') >= 0) return 0;
        int chars = 0;
        foreach (byte b in rest) if ((b & 0xC0) != 0x80) chars++;
        return chars <= 3 ? t.Length - lt : 0;
    }

    /// <summary>The codepage byte the engine turned into codepoint <paramref name="cp"/>, or -1.</summary>
    private static int CodepageByte(int cp)
    {
        if (cp < 0x80) return cp;
        var s = State;
        if (!s.Mapped)
        {
            for (int b = 0; b < s.Codepoints.Length; b++) s.Codepoints[b] = Native.Codepoint((byte)(b + 0x80));
            s.Mapped = true;
        }
        int at = Array.IndexOf(s.Codepoints, cp);
        return at < 0 ? -1 : at + 0x80;
    }
}

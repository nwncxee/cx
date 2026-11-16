using System;
using CX.Engine;

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

    /// <summary>Colours the native text code nests before its ring wraps; deeper opens are ignored here.</summary>
    private const int MaxDepth = 16;

    /// <summary>Edit fields and text blocks one frame can mark. shortcut: past this, later ones draw as plain text
    /// (an edit field's tokens colour, a block's colour stops at its line ends); raise it if a window needs more.</summary>
    internal const int MaxStretches = 1024;

    /// <summary>A stretch of nuklear's command memory one edit field or one text block pushed this frame.</summary>
    internal readonly record struct Stretch(ulong From, ulong To, bool Edit);

    /// <summary>The colour stack a text block carries from line to line, and whether an edit field is drawing.</summary>
    internal sealed class ColorTextState
    {
        public readonly uint[] Stack = new uint[MaxDepth];
        public int Depth;
        public bool InBlock;
        public int Edit;
        /// <summary>The codepage byte for each codepoint 0x80..0xff map to, filled on first use.</summary>
        public readonly int[] Codepoints = new int[128];
        public bool Mapped;
        public readonly Stretch[] Stretches = new Stretch[MaxStretches];
        public int StretchCount;
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

        int depthIn = s.Depth, n = 0, runStart = 0, i = 0;
        bool tokens = false;
        float x = 0f;
        uint color = s.Depth > 0 ? s.Stack[s.Depth - 1] : fg;
        while (i < utf8.Length && n < into.Length - 1)
        {
            int len = Token(utf8[i..], fg, out uint next, out bool open);
            if (len == 0) { i++; continue; }
            tokens = true;
            if (i > runStart)
            {
                into[n++] = new ColorPiece(runStart, i - runStart, x, color);
                x += Measure(utf8[runStart..i], measure);
            }
            if (open) { if (s.Depth < MaxDepth) s.Stack[s.Depth++] = next; }
            else if (s.Depth > 0) s.Depth--;
            color = s.Depth > 0 ? s.Stack[s.Depth - 1] : fg;
            i += len;
            runStart = i;
        }
        if (!tokens && depthIn == 0) return -1;
        if (runStart < utf8.Length && n < into.Length)
            into[n++] = new ColorPiece(runStart, utf8.Length - runStart, x, color);
        return n;
    }

    /// <summary>The width of the text a draw of <paramref name="utf8"/> shows.</summary>
    internal static float Width(ReadOnlySpan<byte> utf8, TextMeasure measure)
    {
        if (!On || utf8.IndexOf((byte)'<') < 0) return Measure(utf8, measure);
        float w = 0f;
        int runStart = 0, i = 0;
        while (i < utf8.Length)
        {
            int len = Token(utf8[i..], 0, out _, out _);
            if (len == 0) { i++; continue; }
            if (i > runStart) w += Measure(utf8[runStart..i], measure);
            i += len;
            runStart = i;
        }
        if (runStart < utf8.Length) w += Measure(utf8[runStart..], measure);
        return w;
    }

    /// <summary>A text block's lines draw between these two: a colour open at a line's end carries to the next.</summary>
    internal static void BeginBlock() { State.InBlock = true; State.Depth = 0; }

    /// <inheritdoc cref="BeginBlock"/>
    internal static void EndBlock() => State.InBlock = false;

    /// <summary>Notes that an edit field (<paramref name="edit"/>) or a text block pushed the commands between buffer
    /// offsets <paramref name="from"/> and <paramref name="to"/>. Commands are pushed in rising offsets, so the
    /// stretches stay sorted.</summary>
    internal static void Mark(ulong from, ulong to, bool edit)
    {
        var s = State;
        if (to <= from || s.StretchCount >= MaxStretches) return;
        if (s.StretchCount > 0 && from < s.Stretches[s.StretchCount - 1].To) return;
        s.Stretches[s.StretchCount++] = new Stretch(from, to, edit);
    }

    /// <summary><see cref="Plan"/> for the queued text command at buffer offset <paramref name="at"/>: an edit field's
    /// text goes ahead as typed, and the lines of one text block carry their colour from one to the next.</summary>
    internal static int PlanAt(ulong at, ReadOnlySpan<byte> utf8, uint fg, TextMeasure measure, Span<ColorPiece> into)
    {
        var s = State;
        int k = Find(at);
        if (k >= 0 && s.Stretches[k].Edit) return -1;
        if (k < 0) EndBlock();
        else if (k != s.LastStretch || !s.InBlock) BeginBlock();
        s.LastStretch = k;
        return Plan(utf8, fg, measure, into);
    }

    /// <summary>The frame's commands are spent: forget its stretches.</summary>
    internal static void EndFrame()
    {
        var s = State;
        s.StretchCount = 0;
        s.LastStretch = -1;
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
    internal static void LeaveEdit() { if (State.Edit > 0) State.Edit--; }

    private static float Measure(ReadOnlySpan<byte> run, TextMeasure measure)
    {
        fixed (byte* p = run) return measure.Width(measure.UserData, measure.Height, p, run.Length);
    }

    /// <summary>The length of the token <paramref name="t"/> starts with, 0 for none. An open token is <c>&lt;c</c>,
    /// three non-zero codepage bytes and <c>&gt;</c>, and gives its colour at <paramref name="fg"/>'s alpha; a close
    /// token is <c>&lt;/c</c> and the one character after it, as the native reader takes four bytes.</summary>
    private static int Token(ReadOnlySpan<byte> t, uint fg, out uint color, out bool open)
    {
        color = 0;
        open = false;
        if (t.Length < 3 || t[0] != (byte)'<') return 0;
        if (t[1] == (byte)'/')
        {
            if (t[2] != (byte)'c') return 0;
            return t.Length == 3 ? 3 : 3 + CharLength(t[3..]);
        }
        if (t[1] != (byte)'c') return 0;
        int at = 2;
        uint rgb = 0;
        for (int k = 0; k < 3; k++)
        {
            if (at >= t.Length) return 0;
            int len = CharLength(t[at..]);
            int b = CodepageByte(t.Slice(at, len));
            if (b <= 0) return 0;
            rgb |= (uint)b << (8 * k);
            at += len;
        }
        if (at >= t.Length || t[at] != (byte)'>') return 0;
        color = rgb | (fg & 0xFF000000u); // not-an-offset: the alpha byte of an nk_color
        open = true;
        return at + 1;
    }

    /// <summary>The byte count of the UTF-8 character <paramref name="t"/> starts with; a stray byte counts as one.</summary>
    private static int CharLength(ReadOnlySpan<byte> t)
    {
        byte b = t[0];
        int len = b < 0xC0 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
        return Math.Min(len, t.Length);
    }

    /// <summary>The codepage byte the engine turned into the UTF-8 character <paramref name="c"/>, or -1.</summary>
    private static int CodepageByte(ReadOnlySpan<byte> c)
    {
        int cp = c.Length switch
        {
            1 => c[0] < 0x80 ? c[0] : -1,
            2 => ((c[0] & 0x1F) << 6) | (c[1] & 0x3F),
            3 => ((c[0] & 0x0F) << 12) | ((c[1] & 0x3F) << 6) | (c[2] & 0x3F),
            _ => -1,
        };
        if (cp < 0x80) return cp;
        var s = State;
        var fn = EngineFn.EncodingSingleCharToUtf8;
        if (fn == null) return cp <= 0xFF ? cp : -1;
        for (int pass = 0; pass < 2; pass++)
        {
            if (!s.Mapped)
            {
                for (int b = 0; b < 128; b++) s.Codepoints[b] = fn((byte)(b + 0x80), 0);
                s.Mapped = true;
            }
            int at = Array.IndexOf(s.Codepoints, cp);
            if (at >= 0 && fn((byte)(at + 0x80), 0) == cp) return at + 0x80;
            s.Mapped = false;
        }
        return -1;
    }
}

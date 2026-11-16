using System.Collections.Generic;
using CX.Services.Chat.Core;
using CX.Services.Chat.Core.Model;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>What the chat examples look at in a built frame: its quads, split into glyphs (a test glyph's box spans u
/// 0..1) and solid fills, and the letters of the line being typed, told apart by their box widths.</summary>
internal static class ChatExamplesKit
{
    /// <summary>A drawn quad: its screen box, whether it is a glyph, and its width.</summary>
    internal readonly record struct Quad(float X0, float Y0, float X1, float Y1, bool Glyph)
    {
        public float W => X1 - X0;
    }

    /// <summary>Every letter a..z: advance 40, box 10 + its alphabet index wide, so a box width names its letter.
    /// Digits are 5 wide and '/' 6, so the character counter is told apart from the letters.</summary>
    internal static GlyphAtlas Atlas()
    {
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        atlas.SetTestGlyph(' ', 40f);
        for (char c = 'a'; c <= 'z'; c++) atlas.SetTestGlyph(c, 40f, 10 + (c - 'a'), 10);
        for (char c = '0'; c <= '9'; c++) atlas.SetTestGlyph(c, 40f, 5, 10);
        atlas.SetTestGlyph('/', 40f, 6, 10);
        return atlas;
    }

    /// <summary>A plain one-tab window that sends on DM: its channel button and tab label are capitals, which the kit
    /// atlas has no glyphs for, so every letter a frame draws is a typed or message letter.</summary>
    internal static ChatWindow Window(float x, float y, float w = 400f, float h = 300f)
    {
        var win = new ChatWindow(x, y, w, h, new[] { new Tab("DM", 0x400) });
        win.ClampSendChannel();
        return win;
    }

    /// <summary>The window's frame, with the typing bar open on <paramref name="text"/> when <paramref name="typing"/>.</summary>
    internal static RenderFrame Build(ChatWindow win, GlyphAtlas atlas, bool typing, string text = "", int caret = -1,
                                      bool hover = false, ChatStore? store = null, bool counter = false)
    {
        win.FooterVisible = typing;
        win.WrapCache = null;
        return WindowRenderer.Build(win, new SettingsWindow(), store ?? new ChatStore(), atlas, typing, typing ? text : "",
                                    caret < 0 ? text.Length : caret, hover, counter: counter);
    }

    /// <summary>The frame's quads, six vertices each.</summary>
    internal static List<Quad> Quads(RenderFrame f)
    {
        var q = new List<Quad>();
        const int Fl = VertexBatch.Floats, PerQuad = 6;
        for (int v = 0; v + PerQuad <= f.Count; v += PerQuad)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            bool glyph = false;
            for (int k = 0; k < PerQuad; k++)
            {
                int o = (v + k) * Fl;
                float x = f.Verts[o], y = f.Verts[o + 1];
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
                if (f.Verts[o + 2] == 1f) glyph = true;
            }
            q.Add(new Quad(x0, y0, x1, y1, glyph));
        }
        return q;
    }

    /// <summary>The box every quad of the frame fits in.</summary>
    internal static (float X0, float Y0, float X1, float Y1) Bounds(RenderFrame f)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (Quad q in Quads(f))
        {
            if (q.X0 < x0) x0 = q.X0; if (q.Y0 < y0) y0 = q.Y0;
            if (q.X1 > x1) x1 = q.X1; if (q.Y1 > y1) y1 = q.Y1;
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>The letter a glyph quad draws, from its box width, or '\0' for anything else.</summary>
    internal static char Letter(Quad q)
    {
        int i = (int)System.MathF.Round(q.W) - 10;
        return q.Glyph && i >= 0 && i < 26 ? (char)('a' + i) : '\0';
    }

    /// <summary>The typed line's letters: every letter glyph the frame draws (no message is in the store).</summary>
    internal static List<Quad> Typed(RenderFrame f, ChatWindow win) => Quads(f).FindAll(q => Letter(q) != '\0');

    /// <summary>The caret: the 2 px wide fill right of the channel button.</summary>
    internal static Quad? Caret(RenderFrame f, ChatWindow win)
    {
        foreach (Quad q in Quads(f))
            if (!q.Glyph && System.MathF.Abs(q.W - 2f) < 1e-3f && q.X0 >= win.X + ChatWindow.DropdownW) return q;
        return null;
    }
}

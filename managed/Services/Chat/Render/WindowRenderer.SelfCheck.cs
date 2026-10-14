using System.Collections.Generic;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;
using static CX.Ui.PanelChrome;
using CX.Services.Chat.Core.Model;

namespace CX.Services.Chat.Render;

public static partial class WindowRenderer
{
    /// <summary>Off-device proof of the portrait-gutter emission: an icon quad is produced for a line that
    /// carries a resref (anchored to its top visual row, in the left gutter) and none for a line without.
    /// Uses a stub atlas (zero-advance glyphs -&gt; one wrap row); real wrapping needs the native bake. Also the
    /// chrome's role mapping, against the hand-written colours chat drew before the shared look.</summary>
    public static void SelfCheck(CX.Framework.ExtenderLog log)
    {
        var check = new Checker(log);

        // Oracles: hand-written literals -- the exact RGBA8 the chatbox drew with before it followed ui.palette
        // -- read back through the role mapping over the slate palette (Theme's own table, not this file's).
        uint[] slate = Theme.Palettes[0].Rgb;
        check.Expect(Theme.Palettes[0].Id == "slate", "slate is the first palette (the one these colours belong to)");
        check.Expect(((slate[(int)Role.PanelBg] << 8) | BgA) == 0x0d0f17bdu, "slate resolves the window background to 0x0d0f17bd");
        check.Expect(((slate[(int)Role.NavOn] << 8) | BgA) == 0x192040bdu, "...a gradient's second colour to 0x192040bd");
        check.Expect(((slate[(int)Role.HeaderBg] << 8) | HeaderA) == 0x1a1f29d9u, "...the header band to 0x1a1f29d9");
        check.Expect(((slate[(int)Role.Accent] << 8) | AccentA) == 0x335785f2u, "...the accent to 0x335785f2");
        check.Expect(((slate[(int)Role.Input] << 8) | InputA) == 0x050508e6u, "...the input bar to 0x050508e6");
        check.Expect(((slate[(int)Role.Selected] << 8) | SelA) == 0x38669eb3u, "...the selection to 0x38669eb3");
        check.Expect((slate[(int)Role.Tx] << 8 | 0xff) == 0xd8dee9ffu, "...the label text to 0xd8dee9ff, opaque");

        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };   // descentGap = 5
        var win = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        var settings = new SettingsWindow();
        var store = new ChatStore();
        store.Append(new ChatLine(1u, "", 0, "", new[] { new ColorRun("plain", 1, 1, 1) }, System.DateTime.UtcNow, "plain"));
        store.Append(new ChatLine(1u, "", 0, "po_x", new[] { new ColorRun("hi", 1, 1, 1) }, System.DateTime.UtcNow, "hi"));

        RenderFrame first = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false);
        check.Expect(first.Skin.Length == 0 && first.SkinOver == 0, "off-device the native art never arms, so no game art is laid");
        var portraits = first.Portraits;
        check.Expect(portraits.Count == 1, "exactly one portrait quad (only the resref-bearing line)");
        if (portraits.Count == 1)
        {
            PortraitQuad q = portraits[0];
            // 20 rows at NWN's 0.64 portrait aspect is 31.25 tall, written out rather than recomputed from
            // the constant the layout under test reads.
            const float ph = 31.25f;
            check.Expect(q.Resref == "po_x", "quad carries the line's resref");
            check.Expect(q.X == ChatWindow.Pad, "quad sits in the left gutter (bx + Pad)");
            check.Expect(q.W == atlas.LineHeight, "quad keeps the gutter width");
            check.Expect(q.H == ph, "quad is grown tall to the portrait aspect");
            // newest line fills the lowest block: its top = body-bottom - BottomPad - block height (padded to the portrait).
            check.Expect(q.Y == win.Y + win.H - BottomPad - ph, "quad anchored to the padded block's top");
        }

        // MeasureBlock (the ingest slide/hold distance): a portrait line is padded up to the portrait height;
        // a plain one-row line is one line high. Both drive the exact per-line compensation on ingest.
        const float phh = 31.25f;
        var pLine = new ChatLine(1u, "", 0, "po_x", new[] { new ColorRun("hi", 1, 1, 1) }, System.DateTime.UtcNow, "hi");
        var tLine = new ChatLine(1u, "", 0, "", new[] { new ColorRun("plain", 1, 1, 1) }, System.DateTime.UtcNow, "plain");
        check.Expect(System.Math.Abs(MeasureBlock(win, pLine, atlas) - phh) < 1e-3, "MeasureBlock pads a portrait line to the portrait height");
        check.Expect(System.Math.Abs(MeasureBlock(win, tLine, atlas) - atlas.LineHeight) < 1e-3, "MeasureBlock of a one-row line is one line high");

        // Settings window open -> portraits still emit (the opaque panel is tail-drawn over them; suppressing
        // them made portraits vanish even when the movable panel sits elsewhere).
        settings.OpenAt(win, 1280, 800);
        check.Expect(Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits.Count == 1,
          "settings window open does not suppress portraits");
        settings.Reset();

        // Right-aligned: portrait flips to the right edge of the text area.
        win.Align = TextAlign.Right;
        var rPortraits = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits;
        check.Expect(rPortraits.Count == 1, "right alignment still emits exactly one portrait quad");
        if (rPortraits.Count == 1)
        {
            float mtw = win.W - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;
            check.Expect(System.Math.Abs(rPortraits[0].X - (ChatWindow.Pad + mtw - atlas.LineHeight)) < 0.1f,
              "right-aligned portrait sits at the right edge");
        }
        win.Align = TextAlign.Left;

        // Toggle off -> no icons even when a resref is present.
        win.Toggles &= ~ChatToggle.Portrait;
        var off = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits;
        check.Expect(off.Count == 0, "portrait toggle off suppresses the icon");

        // Row alignment moved to RowAlign (its own SelfCheck).

        // Layout cache: second build with unchanged content is a cache hit; verts match cold build.
        win.Toggles = ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait;
        win.WrapCache = null;
        var cold = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false);
        check.Expect(win.WrapCache != null, "first build populates the wrap cache");
        var lcRef = win.WrapCache;
        var cached = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false);
        check.Expect(ReferenceEquals(win.WrapCache, lcRef), "second build reuses the cache (not a silent miss)");
        check.Expect(cold.Count == cached.Count, "cache-hit build produces the same vertex count");
        check.Expect(cold.Verts.Length == cached.Verts.Length, "cache-hit build produces same-length verts");
        bool vertsMatch = true;
        for (int vi = 0; vi < cold.Verts.Length && vertsMatch; vi++)
            vertsMatch = System.Math.Abs(cold.Verts[vi] - cached.Verts[vi]) < 1e-3f;
        check.Expect(vertsMatch, "cache-hit verts match cold-build verts");

        // GradientRect corner mapping: 4 distinct colours → verify the winding order.
        {
            var gb = new VertexBatch(0, 0);
            var tl = (1f, 0f, 0f, 1f); var tr = (0f, 1f, 0f, 1f);
            var br = (0f, 0f, 1f, 1f); var bl = (1f, 1f, 0f, 1f);
            gb.GradientRect(0, 0, 10, 10, tl, tr, br, bl);
            float[] gv = gb.ToArray();
            check.Expect(gb.VertexCount == 6, "GradientRect emits 6 vertices");
            check.Expect(gv[4] == 1f && gv[5] == 0f, "V0 carries TL colour");
            check.Expect(gv[12] == 0f && gv[13] == 1f, "V1 carries TR colour");
            check.Expect(gv[20] == 0f && gv[21] == 0f && gv[22] == 1f, "V2 carries BR colour");
            check.Expect(gv[44] == 1f && gv[45] == 1f && gv[46] == 0f, "V5 carries BL colour");
        }

        // A gradient background is the BgGrid² quad grid; BgT is the mapping the grid samples.
        {
            var sw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
            var ss = new SettingsWindow();
            int solidVerts = Build(sw, ss, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            sw.BgMode = BgMode.Linear; sw.WrapCache = null;
            int gradVerts = Build(sw, ss, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            check.Expect(gradVerts - solidVerts == (BgGrid * BgGrid - 1) * 6, "a gradient's grid adds (N*N-1)*6 verts over solid");
            static bool Near(float a, float b) => System.Math.Abs(a - b) < 1e-4f;
            check.Expect(Near(BgT(sw, 0f, -1f, 400, 300), 0f) && Near(BgT(sw, 0f, 1f, 400, 300), 1f) && Near(BgT(sw, -1f, 0f, 400, 300), 0.5f),
              "Linear at the default 90° runs top (0) to bottom (1)");
            sw.BgAngle = 0f;
            check.Expect(Near(BgT(sw, -1f, 0f, 400, 300), 0f) && Near(BgT(sw, 1f, 0f, 400, 300), 1f), "Linear at 0° runs left to right");
            sw.BgAngle = 45f;
            check.Expect(Near(BgT(sw, -1f, -1f, 400, 300), 0f) && Near(BgT(sw, 1f, 1f, 400, 300), 1f), "Linear at 45° pins the corners to 0 and 1");
            sw.BgStart = 0.5f; sw.BgEnd = 0.5f; sw.BgAngle = 90f;
            check.Expect(Near(BgT(sw, 0f, -0.2f, 400, 300), 0f) && Near(BgT(sw, 0f, 0.2f, 400, 300), 1f), "End <= Start is a hard edge at Start");
            sw.BgStart = 0.25f; sw.BgEnd = 0.75f;
            check.Expect(Near(BgT(sw, 0f, -0.9f, 400, 300), 0f) && Near(BgT(sw, 0f, 0f, 400, 300), 0.5f) && Near(BgT(sw, 0f, 0.9f, 400, 300), 1f),
              "stops hold the ends and remap the middle");
            sw.BgStart = 0f; sw.BgEnd = 1f; sw.BgMode = BgMode.Radial;
            check.Expect(Near(BgT(sw, 0f, 0f, 400, 300), 0f) && Near(BgT(sw, 1f, 0f, 400, 300), 1f), "Radial runs centre to edge");
            sw.BgMode = BgMode.Conic; sw.BgAngle = 0f;
            check.Expect(Near(BgT(sw, 1f, 0f, 400, 300), 0f) && Near(BgT(sw, -1f, 0f, 400, 300), 1f)
              && Near(BgT(sw, 0f, 1f, 400, 300), 0.5f) && Near(BgT(sw, 0f, -1f, 400, 300), 0.5f), "Conic is mirrored about the angle (no seam)");
        }

        // Frame auto-hide drops the shadow rings + outline (4 fills each); a hovered tab adds one eased plate.
        {
            var fw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("B", 0xffffffff) }) { AutoHideHeader = false };
            var fs = new SettingsWindow();
            int framed = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            fw.AutoHideFrame = true; fw.WrapCache = null;
            int bare = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            check.Expect(framed - bare == (ShadowAlpha.Length + 1) * 24, "auto-hide frame removes the shadow rings and the outline when the pointer is away");
            int gripped = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true).Count;
            fw.AutoHideFrame = false; fw.WrapCache = null;
            check.Expect(Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true).Count == gripped, "the frame is back while the pointer is inside");
            fw.AutoHideFrame = true; fw.WrapCache = null;
            var t0 = new System.DateTime(2026, 1, 1);
            check.Expect(fw.SetHoverTab(fw.X + fw.TabWFor(2) + 2f, fw.Y + 2f, t0) && fw.HoveredTab == 1 && fw.LeftTab == -1, "hovering tab 1 records it");
            check.Expect(!fw.SetHoverTab(fw.X + fw.TabWFor(2) + 3f, fw.Y + 2f, t0), "staying on the tab changes nothing");
            RenderFrame hf = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true);
            check.Expect(hf.HoverEnd - hf.HoverStart == 6 && hf.LeftEnd == hf.LeftStart && hf.HoverSince == t0, "the hovered tab has a 6-vert plate stamped with the hover time");
            check.Expect(fw.SetHoverTab(float.NaN, fw.Y + 2f, t0.AddMilliseconds(10)) && fw.HoveredTab == -1 && fw.LeftTab == 1, "leaving hands the tab to the fade-out");
            hf = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true);
            check.Expect(hf.HoverEnd == hf.HoverStart && hf.LeftEnd - hf.LeftStart == 6, "the left tab keeps a plate for the fade-out");
            check.Expect(RenderFrame.HoverT(t0.AddMilliseconds(RenderFrame.HoverMs * 0.5), t0) == 0.5f && RenderFrame.HoverT(t0.AddSeconds(1), t0) == 1f, "HoverT eases over HoverMs then clamps");
        }

        log.Info(check.Ok ? "WindowRenderer.SelfCheck: passed" : "WindowRenderer.SelfCheck: FAILED");
    }
}

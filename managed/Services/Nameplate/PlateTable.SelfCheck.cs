using System;
using System.Collections.Generic;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Nameplate;

public sealed partial class PlateTable
{
    /// <summary>Off-device proof of the pure table mechanics that a live client would otherwise be the only
    /// witness to: queue rotation at the cap, decay expiry picking the newest survivor, and the colour pack.
    /// Native reads are not exercised (there is no client here); everything below is id/time arithmetic.</summary>
    public static unsafe void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        check.Expect(Pack(1f, 0f, 0f) == 0xff0000u && Pack(0f, 1f, 0f) == 0x00ff00u && Pack(0f, 0f, 1f) == 0x0000ffu,
          "colour pack puts the channels in RGB order");
        check.Expect(Pack(0.5019608f, 1f, 0.5019608f) == 0x80ff80u, "the uninjured band round-trips to 128,255,128");

        var e = new Entry { Used = true, Id = 7 };
        Fill(e.Lines[0], ColorRunParser.Parse("one"), 1, 5f, 1000); e.LineCount = 1;
        Fill(e.Lines[1], ColorRunParser.Parse("two"), 1, 5f, 2000); e.LineCount = 2;
        check.Expect(e.Lines[0].Runes.Count == 3 && e.Lines[1].Runes.Count == 3, "two queued lines each keep their runes");

        // Decay 5s => hold 3.75s, fade tail 1.25s. Line 0 posted at 1000 is gone at 6000, line 1 at 7000.
        ExpireLines(e, 3000);
        check.Expect(e.LineCount == 2 && e.TopLine == 1, "nothing expires inside the hold window");
        check.Expect(e.Lines[0].Alpha == 1f && !e.Lines[0].Fading, "a line inside its hold is fully opaque and not fading");
        ExpireLines(e, 5000);
        check.Expect(e.LineCount == 2, "a line past its HOLD is still present -- it is fading, not gone");
        check.Expect(e.Lines[0].Fading && e.Lines[0].Alpha > 0f && e.Lines[0].Alpha < 1f, "and it is part way through its fade");
        check.Expect(e.Lines[1].Alpha == 1f, "the newer line is untouched by the older one's fade");
        ExpireLines(e, 6000);
        check.Expect(e.LineCount == 1 && e.TopLine == 0, "the older line releases only once it is transparent");
        check.Expect(e.Lines[0].Runes[0].Value == 't', "the survivor is the NEWER line, in order");
        ExpireLines(e, 8000);
        check.Expect(e.LineCount == 0 && e.TopLine == -1, "both expired leaves no line to draw");

        // Eviction by a newcomer starts a fade; it does NOT overwrite the line.
        var ev = new PlateTable();
        var evp = new Pending { Id = 21, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                                PostedMs = 0, Runs = ColorRunParser.Parse("x") };
        ev.Apply(in evp, 1, 200f);
        Entry? eq = ev.Find(21);
        ev.Apply(new Pending { Id = 21, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                               PostedMs = 1000, Runs = ColorRunParser.Parse("y") }, 1, 200f);
        check.Expect(eq != null && eq.LineCount == 2, "an evicted line is held past the cap while it fades");
        check.Expect(eq!.Lines[0].Fading && eq.Lines[0].Runes[0].Value == 'x', "the evicted line kept its content");
        check.Expect(eq.Lines[1].Runes[0].Value == 'y', "the newcomer is the newest line");
        ExpireLines(eq, 1100);
        check.Expect(eq.LineCount == 2 && eq.Lines[0].Alpha > 0f && eq.Lines[0].Alpha < 1f,
          "half way through the eviction fade the line is still drawn, half transparent");
        ExpireLines(eq, 1300);
        check.Expect(eq.LineCount == 1 && eq.Lines[0].Runes[0].Value == 'y',
          "the evicted line releases once transparent, and the newcomer survives it");

        // A genuinely full array drops the oldest hard rather than refusing the newcomer.
        var fl = new PlateTable();
        for (int i = 0; i < MaxLines; i++)
            fl.Apply(new Pending { Id = 22, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                                   PostedMs = i, Runs = ColorRunParser.Parse(((char)('a' + i)).ToString()) },
                     MaxLines, 200f);
        Entry? fq = fl.Find(22);
        fl.Apply(new Pending { Id = 22, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                               PostedMs = 500, Runs = ColorRunParser.Parse("z") }, MaxLines, 200f);
        check.Expect(fq != null && fq.LineCount == MaxLines, "a full array stays at the ceiling");
        check.Expect(fq!.Lines[MaxLines - 1].Runes[0].Value == 'z', "the newcomer is never refused");
        check.Expect(fq.Lines[0].Runes[0].Value == 'b', "the casualty is the OLDEST line, dropped hard");

        // A pooled slot must not lend a half-faded line to whatever claims it next.
        fq.Release();
        for (int i = 0; i < MaxLines; i++)
            check.Expect(fq.Lines[i].Alpha == 0f && !fq.Lines[i].Fading, "a released slot keeps no residual line alpha");

        check.Expect(NameplateKnobs.Queue.Values[^1] == MaxLines && NameplateKnobs.Lines.Max == MaxWrapped, "the knobs' ceilings are exactly what the table can hold");

        // ---- FadingOut / SortKey: what separation takes part, and in what order ----
        var fo = new Entry { Used = true, Id = 5 };
        fo.Alive = true; fo.Alpha = 0.3f;
        check.Expect(!fo.FadingOut, "a plate fading IN still takes part -- the test is direction, not alpha");
        check.Expect(fo.SortKey == long.MaxValue, "a plate has no send time and sorts as 'now'");
        fo.Alpha = 1f;
        check.Expect(!fo.FadingOut, "a settled plate takes part");
        fo.Alive = false;
        check.Expect(fo.FadingOut, "a plate whose trigger dropped is on its way out and leaves separation");
        fo.Alive = true;
        check.Expect(!fo.FadingOut, "and a re-hover before it disappears rejoins separation");

        var fb = new Entry { Used = true, Id = 6, Alive = true, Alpha = 1f, ShowBubble = true };
        Fill(fb.Lines[0], ColorRunParser.Parse("old"), 1, 5f, 1000);
        Fill(fb.Lines[1], ColorRunParser.Parse("new"), 1, 5f, 4000);
        fb.LineCount = 2;
        ExpireLines(fb, 5000);
        check.Expect(fb.Lines[0].Fading && !fb.Lines[1].Fading, "the older line is in its tail while the newer holds");
        check.Expect(!fb.FadingOut, "a bubble whose NEWEST line is still live takes part, older tails and all");
        check.Expect(fb.SortKey == 4000, "and it sorts on that newest line, so an object that keeps talking stays put");
        ExpireLines(fb, 8000);
        check.Expect(fb.Lines[0].Fading && fb.TopLine == 0, "only the newest line is left, and it is fading");
        check.Expect(fb.FadingOut, "a bubble in its own fade tail leaves separation");
        ExpireLines(fb, 20000);
        check.Expect(fb.TopLine < 0 && fb.FadingOut, "a bubble with nothing left to draw is out too");

        var t = new PlateTable();
        var p = new Pending { Id = 9, Type = NameplateNative.TypeCreature, Runs = ColorRunParser.Parse("a"), DecaySec = 5f, PostedMs = 0, Channel = 1 };
        for (int i = 0; i < 5; i++) t.Apply(in p, 2);
        Entry? q = t.Find(9);
        check.Expect(q != null && LiveCount(q) == 2, "the bubble queue holds at its cap however many lines arrive");
        ExpireLines(q!, 1000);   // the evictions faded at animMs 0, so one age is all it takes to clear them
        check.Expect(q!.LineCount == 2, "and once the evicted lines are transparent the array is back at the cap");

        // Shrinking the cap under a live queue keeps the NEWEST lines, not the oldest survivor.
        var sh = new PlateTable();
        for (int i = 0; i < MaxLines; i++)
            sh.Apply(new Pending { Id = 3, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                                   PostedMs = i, Runs = ColorRunParser.Parse(((char)('a' + i)).ToString()) }, MaxLines);
        Entry? sq = sh.Find(3);
        check.Expect(sq != null && sq.LineCount == MaxLines, "the queue fills to the table ceiling");
        sh.Apply(new Pending { Id = 3, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                               PostedMs = 99, Runs = ColorRunParser.Parse("z") }, 2);
        check.Expect(LiveCount(sq!) == 2, "a shrunken cap takes effect on the next line");
        ExpireLines(sq!, 1000);   // let the lines it evicted finish fading out
        check.Expect(sq!.LineCount == 2, "the over-cap lines fade out rather than vanishing");
        check.Expect(sq.Lines[1].Runes[0].Value == 'z', "the arriving line is the newest");
        check.Expect(sq.Lines[0].Runes[0].Value == 'a' + MaxLines - 1, "the survivor is the NEWEST of the dropped queue, not the oldest");

        // A pooled slot must not lend its cold-resolved strings to the object that claims it next.
        var pool = new PlateTable();
        pool.Apply(new Pending { Id = 11, Type = NameplateNative.TypeCreature, IsPlate = true,
                                 Name = "Alice", Portrait = "po_alice" }, 2);
        Entry? a1 = pool.Find(11);
        check.Expect(a1 != null && a1.PortraitResref == "po_alice", "the first tenant resolves its portrait");
        a1!.Used = false;                                   // the eviction Refresh does on a despawn
        pool.Apply(new Pending { Id = 12, Type = NameplateNative.TypePlaceable, IsPlate = true,
                                 Name = "A barrel", Portrait = "" }, 2);
        Entry? a2 = pool.Find(12);
        check.Expect(ReferenceEquals(a1, a2), "the freed slot is the one reused (the pool has no other empty slot ahead of it)");
        check.Expect(a2!.PortraitResref.Length == 0, "a portraitless newcomer does not inherit the previous tenant's portrait");
        check.Expect(a2.Name == "A barrel", "the newcomer's own name replaces the previous tenant's");
        check.Expect(a2.NameLineCount == 1 && a2.NameLen[0] == 8, "and its line spans with it");

        // A pooled slot must not inherit the previous tenant's LINE state either -- a one-line name in a
        // three-line slot would measure and emit two lines of somebody else's text.
        a2.Used = false;
        pool.Apply(new Pending { Id = 13, Type = NameplateNative.TypePlaceable, IsPlate = true,
                                 Name = "head\nbody\ntail", Portrait = "" }, 2);
        Entry? a3 = pool.Find(13);
        check.Expect(ReferenceEquals(a2, a3) && a3!.NameLineCount == 3, "a broken name indexes as its several lines");
        check.Expect(a3.NameStart[1] == 5 && a3.NameLen[1] == 4, "the middle span is the middle line, LF in neither");
        a3.Used = false;
        pool.Apply(new Pending { Id = 14, Type = NameplateNative.TypePlaceable, IsPlate = true,
                                 Name = "flat", Portrait = "" }, 2);
        check.Expect(pool.Find(14)!.NameLineCount == 1, "the next tenant of that slot is one line, not three");

        t.Clear();
        check.Expect(t.Find(9) == null, "Clear drops every entry (the module-unload edge)");
        pool.Clear();
        check.Expect(a2.PortraitResref.Length == 0 && a2.Name.Length == 0 && a2.NameLineCount == 0,
          "Clear drops the cold-resolved strings and their line spans too, so nothing crosses a module unload");

        var full = new PlateTable();
        long before = full.Overflow;
        for (uint i = 0; i < MaxEntries + 4; i++) full.Apply(new Pending { Id = i + 1, Type = 5 }, 2);
        check.Expect(full.Overflow == before + 4, "a full table refuses new candidates and counts them");

        // Aurora colour runs survive the flatten: <c> + 3 raw RGB bytes + '>' ... </c>.
        var col = new BubbleLine();
        Fill(col, ColorRunParser.Parse("<c" + (char)0xff + (char)0x20 + (char)0x20 + ">hi</c>"), 1, 5f, 0);
        check.Expect(col.Runes.Count == 2 && col.Colours[0] == 0xff2020u && col.Colours[1] == 0xff2020u,
          "a colour run reaches the per-rune colour array");

        // The ellipsis fit, on a constant advance -- EnsureWrap itself needs a baked atlas, this does not.
        var el = new BubbleLine();
        for (int i = 0; i < 10; i++) el.Runes.Add(new Rune((char)('a' + i)));
        el.Spans[0] = new TextWrapper.LineSpan(0, 5);
        el.Spans[1] = new TextWrapper.LineSpan(5, 10);
        el.SpanCount = 2;
        static float Ten(Rune _) => 10f;
        el.Truncated = false;
        check.Expect(FitSpans(el, 100f, Ten) == 50f, "an uncut line measures its widest span and keeps every rune");
        el.Truncated = true;
        check.Expect(FitSpans(el, 100f, Ten) == 80f && el.Spans[1].End == 10,
          "a cut line that has room for the ellipsis keeps every rune and reports the wider line");
        el.Spans[1] = new TextWrapper.LineSpan(5, 10);
        check.Expect(FitSpans(el, 60f, Ten) == 60f && el.Spans[1].End == 8,
          "a cut line trims its last span until the ellipsis fits the wrap width");
        el.Spans[1] = new TextWrapper.LineSpan(5, 10);
        check.Expect(FitSpans(el, 5f, Ten) == 50f && el.Spans[1].End == el.Spans[1].Start,
          "a width narrower than the ellipsis itself empties the last span rather than looping");

        // ---- EnsureNameWrap: the plate name wraps to the same width a bubble does ----
        // A constant 10px advance, so 45px fits four runes and the ellipsis is 30px wide.
        int adv = 0;
        Func<Rune, float> A10 = _ => { adv++; return 10f; };
        var wn = new Entry();
        string Line(Entry x, int i) => x.Name.Substring(x.NameStart[i], x.NameLen[i]);

        SetName(wn, "Bob");
        EnsureNameWrap(wn, A10, 100f, 1);
        check.Expect(wn.NameLineCount == 1 && Line(wn, 0) == "Bob" && !wn.NameTruncated,
          "a name that fits the width is one line, exactly as before");

        SetName(wn, "aaa bbb");
        EnsureNameWrap(wn, A10, 45f, 1);
        check.Expect(wn.NameLineCount == 2 && Line(wn, 0) == "aaa" && Line(wn, 1) == "bbb",
          "a name too wide for the column wraps -- the width knob now bounds the plate, not just the bubble");

        // Order: the author's break first, then the wrap INSIDE each resulting line. Never the other way.
        SetName(wn, PlateLayout.BreakName("hdr   aaa bbb"));
        check.Expect(wn.Name == "hdr\naaa bbb", "the padded name still breaks where its author asked");
        EnsureNameWrap(wn, A10, 45f, 1);
        check.Expect(wn.NameLineCount == 3 && Line(wn, 0) == "hdr" && Line(wn, 1) == "aaa" && Line(wn, 2) == "bbb",
          "an author break is honoured and the wrap only adds breaks inside a too-long line");
        check.Expect(wn.Name == "hdr\naaa bbb", "and the wrap leaves the STRING alone -- no wrap break can reach /tell");

        // The cache key: a steady frame rewraps nothing, a width change rewraps, an atlas re-bake rewraps.
        int atCall = adv;
        EnsureNameWrap(wn, A10, 45f, 1);
        check.Expect(adv == atCall, "a second call at the same width/epoch/scale measures nothing at all");
        EnsureNameWrap(wn, A10, 100f, 1);
        check.Expect(adv > atCall && wn.NameLineCount == 2, "a WIDTH change rewraps -- the knob is live, not dead");
        atCall = adv;
        EnsureNameWrap(wn, A10, 100f, 2);
        check.Expect(adv > atCall, "an atlas re-bake (epoch) rewraps: every advance changed");
        SetName(wn, "other");
        EnsureNameWrap(wn, A10, 100f, 2);
        check.Expect(wn.NameLineCount == 1 && Line(wn, 0) == "other", "a new name invalidates the cache at the same key");
        atCall = adv;
        SetName(wn, "other");
        check.Expect(adv == atCall && wn.NameWrapWidth == 100f, "re-setting the SAME name does not invalidate the wrap");

        // Over the wrapped-line ceiling: the same graceful ellipsis a cut bubble gets.
        var sb = new StringBuilder();
        for (int i = 0; i < MaxWrapped + 8; i++) { if (i > 0) sb.Append(' '); sb.Append("aaa"); }
        SetName(wn, sb.ToString());
        EnsureNameWrap(wn, A10, 45f, 1);
        check.Expect(wn.NameLineCount == MaxWrapped && wn.NameTruncated,
          "a name past the wrapped-line ceiling stops at it and says so");
        check.Expect(wn.NameLen[MaxWrapped - 1] == 1,
          "and its last line is trimmed until the three-dot ellipsis fits the width beside it");

        SetName(wn, "");
        EnsureNameWrap(wn, A10, 45f, 1);
        check.Expect(wn.NameLineCount == 0 && !wn.NameTruncated, "an empty name is no lines, not one empty one");
        SetName(wn, "aaa bbb");
        EnsureNameWrap(wn, A10, 0f, 1);
        check.Expect(wn.NameLineCount == 1, "an unknown width leaves the author's breaks as the only ones");

        // Alignment: LineX treats a wrap-produced line exactly as a break-produced one.
        SetName(wn, "aaa bbb");
        EnsureNameWrap(wn, A10, 45f, 3);
        check.Expect(wn.NameLineCount == 2 && Line(wn, 1) == "bbb", "a two-line wrap for the alignment cases");
        check.Expect(PlateLayout.LineX(20f, 50f, 50f) == 20f,
          "the widest wrapped line starts at the column, wrap-produced or break-produced");
        check.Expect(PlateLayout.LineX(20f, 50f, 20f) == 35f,
          "and a shorter one centres in the text column -- on a creature plate as on any other");

        // ---- ObjectTextBubbleOverride: the module's own plate text ----
        var ov = new Entry();
        string Para(int i)
        {
            var p = new StringBuilder();
            foreach (Rune r in ov.Paras[i].Runes) p.Append(r.ToString());
            return p.ToString();
        }
        const string Grey = "<c\u0080\u0080\u0080>";   // the Aurora grammar: '<c' + three raw RGB bytes + '>'

        SetOverride(ov, 0, "");
        check.Expect(ov.ParaCount == 0, "no override is no paragraphs");
        SetOverride(ov, 3, "");
        check.Expect(ov.ParaCount == 0, "an EMPTY override leaves the plate untouched whatever the mode says");
        SetOverride(ov, 0, " Poisoned");
        check.Expect(ov.ParaCount == 0, "bubble mode 0 with text is still no override -- the engine's default arm changes nothing");

        // The server end's own two test vectors (the reference server's ComposeLabels tests).
        SetOverride(ov, 3, " Poisoned Diseased\nSneaking Invisible");
        check.Expect(ov.ParaCount == 2 && Para(0) == " Poisoned Diseased" && Para(1) == "Sneaking Invisible",
          "append splits into the injury-line splice and the line below it, the server's leading space intact");
        SetOverride(ov, 3, "\nSneaking");
        check.Expect(ov.ParaCount == 2 && Para(0).Length == 0 && Para(1) == "Sneaking",
          "an override opening with a break has an EMPTY splice -- nothing joins the injury line");
        SetOverride(ov, 3, " Poisoned\r\nSneaking");
        check.Expect(ov.ParaCount == 2 && Para(1) == "Sneaking", "CRLF is ONE break, not two");
        SetOverride(ov, 2, "Hidden\n");
        check.Expect(ov.OverrideMode == 2 && ov.ParaCount == 1 && Para(0) == "Hidden",
          "a trailing break is not a blank line");
        SetOverride(ov, 1, "The Sign");
        check.Expect(ov.OverrideMode == 1 && ov.ParaCount == 1 && Para(0) == "The Sign",
          "replace mode carries the whole plate string, name and band included");

        SetOverride(ov, 3, " " + Grey + "Sneaking</c>");
        check.Expect(Para(0) == " Sneaking", "the colour grammar is parsed OFF the text, not drawn as tag bytes");
        check.Expect(ov.Paras[0].Colours[0] == 0xffffffu && ov.Paras[0].Colours[1] == 0x808080u,
          "and the tag's colour lands on the glyphs it wrapped, the splice space staying plain");

        // The change key. There is no hook for the override, so this cache is what the per-frame poll rides on.
        ov.Paras[0].WrapWidth = 123f;
        SetOverride(ov, 3, " " + Grey + "Sneaking</c>");
        check.Expect(ov.Paras[0].WrapWidth == 123f, "re-setting the SAME override re-parses nothing and keeps the wrap");
        SetOverride(ov, 3, " Sneaking Invisible");
        check.Expect(ov.Paras[0].WrapWidth == -1f && Para(0) == " Sneaking Invisible",
          "a CHANGED override re-resolves -- a player who starts sneaking is not cached away forever");
        ov.Paras[0].WrapWidth = 123f;
        SetOverride(ov, 2, " Sneaking Invisible");
        check.Expect(ov.Paras[0].WrapWidth == -1f && ov.OverrideMode == 2,
          "a changed MODE at the same text re-resolves too");

        SetName(ov, "Bob");
        SetOverride(ov, 3, " Sneaking");
        check.Expect(ov.Name == "Bob" && ov.NameLineCount == 1,
          "the override never touches Name, so the /tell prefill cannot pick up a 'Sneaking' suffix");

        var sb3 = new StringBuilder("x");
        for (int i = 0; i < MaxParas + 3; i++) sb3.Append("\nrow");
        SetOverride(ov, 3, sb3.ToString());
        check.Expect(ov.ParaCount == MaxParas, "an override past the paragraph ceiling stops at it rather than growing");

        Func<uint, string> engineName = _ => "engine name";
        byte* placeable = stackalloc byte[EngineLayout.PlaceableNameLen + sizeof(uint)];
        System.Runtime.InteropServices.NativeMemory.Clear(placeable, (nuint)(EngineLayout.PlaceableNameLen + sizeof(uint)));
        check.Expect(RawName((IntPtr)placeable, NameplateNative.TypePlaceable, 1, engineName) == "",
                     "a nameless placeable plates blank, never the engine's 2DA fallback");
        byte* bench = stackalloc byte[] { (byte)'B', (byte)'e', (byte)'n', (byte)'c', (byte)'h' };
        CX.Engine.CNWCPlaceable view = new((IntPtr)placeable);
        view.Name = (IntPtr)bench;
        view.NameLen = 5;
        check.Expect(RawName((IntPtr)placeable, NameplateNative.TypePlaceable, 1, engineName) == "Bench",
                     "a named placeable plates its own name");
        check.Expect(RawName(IntPtr.Zero, NameplateNative.TypeItem, 1, engineName) == "engine name",
                     "an item still asks the engine for its name");

        log.Info(check.Ok ? "PlateTable.SelfCheck: passed" : "PlateTable.SelfCheck: FAILED");
    }
}

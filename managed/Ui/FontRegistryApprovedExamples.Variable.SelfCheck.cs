using System.Buffers.Binary;
using CX.Framework;
using CX.Services.Panels;

namespace CX.Ui;

internal static partial class FontRegistryApprovedExamples
{
    /// <summary>Font units an instanced point or advance may sit from fontTools' own: its rounding of each delta.</summary>
    private const int OutlineSlack = 1;

    /// <summary>A variable font at the picked weight, against fontTools' instancer.</summary>
    private static void Variable(Checker check, string dir)
    {
        Dictionary<(int Weight, int Cp), (int Advance, string Points)> golden = Golden();

        // E6 Given a chat window whose typeface is Noto Sans, installed as one variable-weight file When the player sets
        // its Weight to 700 Then the window's letters are Noto Sans Bold's: the same shapes and widths as Noto Sans's own
        // Bold weight, not Regular's
        Example(check, "E6 Noto Sans at 700 has Bold's outlines", () =>
        {
            string key = FontRegistry.FaceKey(Noto, 700, Normal);
            return Drawn(key) == "NotoSans-VF.ttf#0@700" && FontRegistry.Bytes(key) is byte[] b && Matches(b, 700, golden, out _)
                && !Matches(b, 400, golden, out _);
        });

        // E7 Given a chat window whose typeface is Noto Sans, variable from 100 to 900 When the player sets its Weight to
        // 300, which Noto Sans names Light Then the letters are Noto Sans Light's, and at every other step they are that
        // weight of Noto Sans, lighter at each lower step and heavier at each higher one
        Example(check, "E7 every step of Noto Sans has that weight's outlines", () =>
        {
            var off = new List<string>();
            foreach (int w in Ladder)
            {
                string why = "no bytes";
                if (FontRegistry.Bytes(FontRegistry.FaceKey(Noto, w, Normal)) is not byte[] b || !Matches(b, w, golden, out why)) off.Add($"{w}: {why}");
            }
            return off.Count == 0;
        });

        // E8 Given a variable font whose outlines are in CFF2, such as Cantarell's variable file When the player opens a
        // Typeface list Then none of its weights are offered, rather than a choice that draws in the default font
        Example(check, "E8 a CFF2 variable font is not offered", () =>
            !FontRegistry.Names.Any(n => n.Contains("Cantarell")) && FontRegistry.Names.Contains(Noto));

        // E36 Given a hak supplies a variable-weight font as the NUI skin's font When the player sets Default weight 700
        // or a card asks for bold Then that font draws at its default instance, exactly as before
        Example(check, "E36 a hak's variable font stays at its default", () =>
        {
            byte[] hak = Fixture("NotoSans-VF.ttf");
            FontRegistry.Declare("cx-fontcheck-hakvf", hak, "a check's stand-in for a hak's variable font");
            try
            {
                return ReferenceEquals(FontRegistry.Bytes(FontRegistry.FaceKey("cx-fontcheck-hakvf", 700, Normal)), hak)
                    && ReferenceEquals(FontRegistry.Bytes(FontRegistry.RunFace("cx-fontcheck-hakvf", 400, Normal, 700, 0, false, out _)), hak);
            }
            finally { FontRegistry.Undeclare("cx-fontcheck-hakvf"); }
        });

        // E37 Given a card in Noto Sans, a variable file, at Default weight 300 When it draws a bold run and a run asking
        // for 600 Then the bold run is Noto Sans Bold, the other is Noto Sans SemiBold, and the rest is Noto Sans Light
        Example(check, "E37 a weight text asks for on a variable face", () =>
        {
            if (!PanelMarkup.TryParse("rest <b>bold</b> <w=600>semi</w>", out Run[] runs, out _) || runs.Length != 4) return false;
            return WithUi(Noto, 300, Normal, () =>
            {
                string rest = PanelFonts.RunFace(runs[0], out _), bold = PanelFonts.RunFace(runs[1], out _), semi = PanelFonts.RunFace(runs[3], out _);
                return Drawn(rest) == "NotoSans-VF.ttf#0@300" && Drawn(bold) == "NotoSans-VF.ttf#0@700" && Drawn(semi) == "NotoSans-VF.ttf#0@600"
                    && FontRegistry.Bytes(rest) is byte[] r && Matches(r, 300, golden, out _)
                    && FontRegistry.Bytes(bold) is byte[] b && Matches(b, 700, golden, out _)
                    && FontRegistry.Bytes(semi) is byte[] m && Matches(m, 600, golden, out _);
            });
        });

        // E38 Given Noto Sans, variable in weight and in width When the Weight is 700 Then letters and line length match
        // Noto Sans Bold at normal width, never a condensed instance
        Example(check, "E38 the width stays normal", () =>
        {
            const string Line = "Hamburgefonstiv";
            if (FontRegistry.Bytes(FontRegistry.FaceKey(Noto, 700, Normal)) is not byte[] b) return false;
            int line = 0, want = 0;
            foreach (char ch in Line)
            {
                if (Outline(b, ch) is not (int a, _)) return false;
                line += a;
                want += golden[(700, ch)].Advance;
            }
            return Math.Abs(line - want) <= OutlineSlack * Line.Length && Matches(b, 700, golden, out _);
        });

        // E39 Given a variable font whose weight runs 300 to 700 When the player picks 900 or 100 Then it draws at 700 or
        // 300, its own ends, with no thickened or thinned letters
        Example(check, "E39 a weight past the font's range draws at its ends", () =>
        {
            FontCatalog wide = FontRegistry.UseCatalog(FontCatalog.Scan(null, new[] { Path.Combine(dir, "narrow") }));
            try
            {
                string heavy = FontRegistry.FaceKey(Noto, 900, Normal), light = FontRegistry.FaceKey(Noto, 100, Normal);
                return Drawn(heavy) == "NotoSans-300-700-VF.ttf#0@700" && Drawn(light) == "NotoSans-300-700-VF.ttf#0@300"
                    && FontRegistry.Bytes(heavy) is byte[] h && Matches(h, 700, golden, out _)
                    && FontRegistry.Bytes(light) is byte[] l && Matches(l, 300, golden, out _);
            }
            finally { FontRegistry.UseCatalog(wide); }
        });
    }

    /// <summary>fontTools' instance of each fixture glyph at each weight: advance, then its contours' points.</summary>
    private static Dictionary<(int, int), (int, string)> Golden()
    {
        var d = new Dictionary<(int, int), (int, string)>();
        foreach (string line in System.Text.Encoding.UTF8.GetString(Fixture("NotoSans-VF.golden.tsv")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.Split('\t');
            d[(int.Parse(f[0]), Convert.ToInt32(f[1][2..], 16))] = (int.Parse(f[2]), f[3]);
        }
        return d;
    }

    /// <summary>True when every glyph of <paramref name="font"/> the golden holds at <paramref name="weight"/> has its
    /// advance and points within <see cref="OutlineSlack"/>; <paramref name="why"/> names the first that does not.</summary>
    private static bool Matches(byte[] font, int weight, Dictionary<(int, int), (int Advance, string Points)> golden, out string why)
    {
        int seen = 0;
        foreach (((int w, int cp), (int adv, string pts)) in golden)
        {
            if (w != weight) continue;
            seen++;
            if (Outline(font, cp) is not (int a, List<List<(int X, int Y, bool On)>> got)) { why = $"U+{cp:X4} unreadable"; return false; }
            if (Math.Abs(a - adv) > OutlineSlack) { why = $"U+{cp:X4} advance {a}, fontTools {adv}"; return false; }
            string[] contours = pts.Split(" | ");
            if (contours.Length != got.Count) { why = $"U+{cp:X4} {got.Count} contours, fontTools {contours.Length}"; return false; }
            for (int c = 0; c < contours.Length; c++)
            {
                string[] p = contours[c].Split(' ');
                if (p.Length != got[c].Count) { why = $"U+{cp:X4} contour {c} has {got[c].Count} points, fontTools {p.Length}"; return false; }
                for (int i = 0; i < p.Length; i++)
                {
                    string[] xyo = p[i].Split(',');
                    (int x, int y, bool on) = got[c][i];
                    if (Math.Abs(x - int.Parse(xyo[0])) > OutlineSlack || Math.Abs(y - int.Parse(xyo[1])) > OutlineSlack || on != (xyo[2] == "1"))
                    { why = $"U+{cp:X4} point {i} at {x},{y}, fontTools {xyo[0]},{xyo[1]}"; return false; }
                }
            }
        }
        why = seen == 0 ? $"no golden at {weight}" : "";
        return seen > 0;
    }

    /// <summary>The advance and outline of <paramref name="cp"/> in one static TrueType font, a composite's parts moved by
    /// their offsets; null for anything else. The check's own reader, apart from the code that writes the font.</summary>
    private static (int Advance, List<List<(int X, int Y, bool On)>> Contours)? Outline(byte[] f, int cp)
    {
        int Table(uint tag)
        {
            int n = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4));
            for (int i = 0; i < n; i++)
                if (BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(12 + i * 16)) == tag) return (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(12 + i * 16 + 8));
            return -1;
        }
        int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(at));
        int S16(int at) => BinaryPrimitives.ReadInt16BigEndian(f.AsSpan(at));
        static uint Tag(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);
        int cmap = Table(Tag("cmap")), head = Table(Tag("head")), hhea = Table(Tag("hhea")), hmtx = Table(Tag("hmtx")), loca = Table(Tag("loca")), glyf = Table(Tag("glyf"));
        if (cmap < 0 || head < 0 || hhea < 0 || hmtx < 0 || loca < 0 || glyf < 0) return null;
        int gid = -1;
        for (int i = 0, n = U16(cmap + 2); i < n && gid < 0; i++)
        {
            int sub = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(cmap + 4 + i * 8 + 4));
            if (U16(sub) != 4) continue;
            int segs = U16(sub + 6) / 2, ends = sub + 14, starts = ends + segs * 2 + 2, deltas = starts + segs * 2, ranges = deltas + segs * 2;
            for (int s = 0; s < segs; s++)
            {
                if (cp > U16(ends + s * 2) || cp < U16(starts + s * 2)) continue;
                int ro = U16(ranges + s * 2);
                int g = ro == 0 ? cp : U16(ranges + s * 2 + ro + (cp - U16(starts + s * 2)) * 2);
                gid = g == 0 ? 0 : (g + S16(deltas + s * 2)) & 0xFFFF;
                break;
            }
        }
        if (gid <= 0) return null;
        int metrics = U16(hhea + 34);
        int advance = U16(hmtx + Math.Min(gid, metrics - 1) * 4);
        bool longLoca = S16(head + 50) == 1;
        int At(int g) => longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(loca + g * 4)) : U16(loca + g * 2) * 2;
        var contours = new List<List<(int, int, bool)>>();
        void Glyph(int g, int dx, int dy, int depth)
        {
            int at = glyf + At(g);
            if (At(g + 1) == At(g) || depth > 8) return;
            int nc = S16(at);
            if (nc < 0)
            {
                int p = at + 10, flags;
                do
                {
                    flags = U16(p); int part = U16(p + 2); p += 4;
                    int a1, a2;
                    if ((flags & 1) != 0) { a1 = S16(p); a2 = S16(p + 2); p += 4; } else { a1 = (sbyte)f[p]; a2 = (sbyte)f[p + 1]; p += 2; }
                    p += (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
                    Glyph(part, dx + ((flags & 2) != 0 ? a1 : 0), dy + ((flags & 2) != 0 ? a2 : 0), depth + 1);
                } while ((flags & 0x20) != 0);
                return;
            }
            var endPts = new int[nc];
            for (int i = 0; i < nc; i++) endPts[i] = U16(at + 10 + i * 2);
            int count = nc == 0 ? 0 : endPts[^1] + 1;
            int q = at + 10 + nc * 2;
            q += 2 + U16(q);
            var fl = new byte[count];
            for (int i = 0; i < count;)
            {
                byte b = f[q++]; fl[i++] = b;
                if ((b & 8) != 0) for (int r = f[q++]; r > 0; r--) fl[i++] = b;
            }
            var xs = new int[count]; var ys = new int[count];
            for (int axis = 0; axis < 2; axis++)
            {
                int v = 0, shortBit = axis == 0 ? 2 : 4, same = axis == 0 ? 16 : 32;
                for (int i = 0; i < count; i++)
                {
                    if ((fl[i] & shortBit) != 0) { int d = f[q++]; v += (fl[i] & same) != 0 ? d : -d; }
                    else if ((fl[i] & same) == 0) { v += S16(q); q += 2; }
                    (axis == 0 ? xs : ys)[i] = v;
                }
            }
            for (int c = 0, start = 0; c < nc; start = endPts[c] + 1, c++)
            {
                var pts = new List<(int, int, bool)>();
                for (int i = start; i <= endPts[c]; i++) pts.Add((xs[i] + dx, ys[i] + dy, (fl[i] & 1) != 0));
                contours.Add(pts);
            }
        }
        Glyph(gid, 0, 0, 0);
        return (advance, contours);
    }
}

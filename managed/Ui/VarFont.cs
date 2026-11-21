using System.Buffers.Binary;

namespace CX.Ui;

/// <summary>A variable TrueType font made static at one weight, for the engine's stb_truetype, which reads only a
/// font's default outlines: <c>gvar</c>'s deltas applied (untouched points interpolated, <c>avar</c> honoured), advances
/// from its phantom points, every other axis at its default. Cold: a pick of a weight, never a frame.</summary>
internal static class VarFont
{
    /// <summary>Tables the static font leaves out: the variations themselves, and caches and hinting the new outlines
    /// would make stale.</summary>
    private static readonly string[] Dropped = { "fvar", "gvar", "avar", "HVAR", "MVAR", "VVAR", "STAT", "cvar", "DSIG", "hdmx", "LTSH", "VDMX", "cvt ", "fpgm", "prep" };
    /// <summary>The weight axis's tag.</summary>
    private const string Wght = "wght";
    /// <summary>A glyph's four phantom points, after its outline points: left side, advance, top, bottom.</summary>
    private const int Phantoms = 4;
    /// <summary>F2DOT14's one: the normalised coordinate's unit.</summary>
    private const double F2Dot14 = 16384.0;

    /// <summary>The component flags the rewrite reads or sets.</summary>
    private const int ArgWords = 0x1, ArgsXy = 0x2, Scale = 0x8, More = 0x20, XyScale = 0x40, TwoByTwo = 0x80, Instructions = 0x100;
    /// <summary>gvar's tuple flags.</summary>
    private const int EmbeddedPeak = 0x8000, Intermediate = 0x4000, PrivatePoints = 0x2000, TupleIndexMask = 0x0FFF, SharedPoints = 0x8000, CountMask = 0x0FFF;

    /// <summary>The font at <paramref name="index"/> of <paramref name="font"/> at weight <paramref name="weight"/>, every
    /// other axis at its default, as a static font; null when it is not a variable TrueType font.</summary>
    public static byte[]? Instance(byte[] font, int index, int weight)
    {
        try
        {
            var f = new Reader(font, Sfnt.Offset(font, index));
            if (!f.Has("fvar") || !f.Has("gvar") || !f.Has("glyf") || !f.Has("loca") || !f.Has("hmtx")) return null;
            double[] at = Normalize(f, weight);
            Glyph[] glyphs = Glyphs(f);
            ApplyVariations(f, glyphs, at);
            return Write(f, glyphs);
        }
        catch (ArgumentOutOfRangeException) { return null; }   // a table pointing past the file
    }

    /// <summary>One glyph as read and varied: an outline's points (or a composite's component offsets) followed by the
    /// four phantoms, and the deltas the variations add to each.</summary>
    private sealed class Glyph
    {
        public int Contours;
        public int[] Ends = Array.Empty<int>();
        public double[] X = Array.Empty<double>(), Y = Array.Empty<double>();
        public double[] Dx = Array.Empty<double>(), Dy = Array.Empty<double>();
        public bool[] On = Array.Empty<bool>();
        /// <summary>A composite's component records, as read: flags, glyph, args, and the transform bytes after them.</summary>
        public List<(int Flags, int Glyph, int A1, int A2, byte[] Transform)>? Parts;
        public int XMin;
        public bool Empty => Contours == 0 && Parts == null;
        public int Points => Parts?.Count ?? X.Length - Phantoms;
    }

    /// <summary>Each axis's coordinate in the normalised -1..1 space: the weight axis at <paramref name="weight"/>
    /// clamped to it, every other at its default, mapped through <c>avar</c> when the font has one.</summary>
    private static double[] Normalize(Reader f, int weight)
    {
        int fvar = f.Table("fvar");
        int axes = fvar + f.U16(fvar + 4), count = f.U16(fvar + 8), size = f.U16(fvar + 10);
        var at = new double[count];
        for (int a = 0; a < count; a++)
        {
            int rec = axes + a * size;
            double min = f.S32(rec + 4) / 65536.0, def = f.S32(rec + 8) / 65536.0, max = f.S32(rec + 12) / 65536.0;
            double v = f.Tag(rec) == Wght ? Math.Clamp(weight, min, max) : def;
            double n = v < def ? (def == min ? 0 : (v - def) / (def - min)) : (max == def ? 0 : (v - def) / (max - def));
            at[a] = Math.Round(n * F2Dot14) / F2Dot14;
        }
        if (!f.Has("avar")) return at;
        int p = f.Table("avar") + 8;
        for (int a = 0; a < Math.Min(count, f.U16(f.Table("avar") + 6)); a++)
        {
            int maps = f.U16(p);
            p += 2;
            double v = at[a];
            for (int i = 1; i < maps; i++)
            {
                double from0 = f.S16(p + (i - 1) * 4) / F2Dot14, to0 = f.S16(p + (i - 1) * 4 + 2) / F2Dot14;
                double from1 = f.S16(p + i * 4) / F2Dot14, to1 = f.S16(p + i * 4 + 2) / F2Dot14;
                if (v < from0 || v > from1) continue;
                at[a] = from1 == from0 ? to0 : to0 + (v - from0) * (to1 - to0) / (from1 - from0);
                break;
            }
            at[a] = Math.Round(at[a] * F2Dot14) / F2Dot14;
            p += maps * 4;
        }
        return at;
    }

    /// <summary>Every glyph's points and phantoms, from <c>glyf</c>, <c>loca</c> and <c>hmtx</c>.</summary>
    private static Glyph[] Glyphs(Reader f)
    {
        int n = f.U16(f.Table("maxp") + 4), loca = f.Table("loca"), glyf = f.Table("glyf"), hmtx = f.Table("hmtx");
        bool longLoca = f.S16(f.Table("head") + 50) == 1;
        int metrics = f.U16(f.Table("hhea") + 34);
        int Loc(int g) => longLoca ? (int)f.U32(loca + g * 4) : f.U16(loca + g * 2) * 2;
        var all = new Glyph[n];
        for (int g = 0; g < n; g++)
        {
            var gl = all[g] = new Glyph();
            int adv = f.U16(hmtx + Math.Min(g, metrics - 1) * 4);
            int lsb = g < metrics ? f.S16(hmtx + g * 4 + 2) : f.S16(hmtx + metrics * 4 + (g - metrics) * 2);
            var xs = new List<double>(); var ys = new List<double>();
            if (Loc(g + 1) > Loc(g))
            {
                int at = glyf + Loc(g);
                gl.Contours = f.S16(at);
                gl.XMin = f.S16(at + 2);
                if (gl.Contours < 0) ReadComposite(f, at + 10, gl, xs, ys); else ReadSimple(f, at, gl, xs, ys);
            }
            double left = gl.XMin - lsb;
            xs.Add(left); ys.Add(0); xs.Add(left + adv); ys.Add(0); xs.Add(0); ys.Add(0); xs.Add(0); ys.Add(0);
            gl.X = xs.ToArray(); gl.Y = ys.ToArray();
            gl.Dx = new double[gl.X.Length]; gl.Dy = new double[gl.X.Length];
        }
        return all;
    }

    private static void ReadSimple(Reader f, int at, Glyph gl, List<double> xs, List<double> ys)
    {
        gl.Ends = new int[gl.Contours];
        for (int i = 0; i < gl.Contours; i++) gl.Ends[i] = f.U16(at + 10 + i * 2);
        int count = gl.Contours == 0 ? 0 : gl.Ends[^1] + 1;
        int p = at + 10 + gl.Contours * 2;
        p += 2 + f.U16(p);
        var flags = new byte[count];
        for (int i = 0; i < count;)
        {
            byte b = f.B(p++);
            flags[i++] = b;
            if ((b & 8) != 0) for (int r = f.B(p++); r > 0 && i < count; r--) flags[i++] = b;
        }
        for (int axis = 0; axis < 2; axis++)
        {
            int v = 0, shortBit = axis == 0 ? 2 : 4, same = axis == 0 ? 16 : 32;
            for (int i = 0; i < count; i++)
            {
                if ((flags[i] & shortBit) != 0) { int d = f.B(p++); v += (flags[i] & same) != 0 ? d : -d; }
                else if ((flags[i] & same) == 0) { v += f.S16(p); p += 2; }
                (axis == 0 ? xs : ys).Add(v);
            }
        }
        gl.On = Array.ConvertAll(flags, b => (b & 1) != 0);
    }

    private static void ReadComposite(Reader f, int p, Glyph gl, List<double> xs, List<double> ys)
    {
        gl.Parts = new();
        int flags;
        do
        {
            flags = f.U16(p);
            int glyph = f.U16(p + 2);
            p += 4;
            int a1, a2;
            if ((flags & ArgWords) != 0)
            {
                bool signed = (flags & ArgsXy) != 0;
                a1 = signed ? f.S16(p) : f.U16(p); a2 = signed ? f.S16(p + 2) : f.U16(p + 2); p += 4;
            }
            else
            {
                bool signed = (flags & ArgsXy) != 0;
                a1 = signed ? (sbyte)f.B(p) : f.B(p); a2 = signed ? (sbyte)f.B(p + 1) : f.B(p + 1); p += 2;
            }
            int t = (flags & Scale) != 0 ? 2 : (flags & XyScale) != 0 ? 4 : (flags & TwoByTwo) != 0 ? 8 : 0;
            gl.Parts.Add((flags, glyph, a1, a2, f.Slice(p, t)));
            p += t;
            xs.Add((flags & ArgsXy) != 0 ? a1 : 0); ys.Add((flags & ArgsXy) != 0 ? a2 : 0);
        } while ((flags & More) != 0);
    }

    /// <summary>Adds every tuple's deltas, scaled by how far <paramref name="at"/> sits inside its region, to each glyph.</summary>
    private static void ApplyVariations(Reader f, Glyph[] glyphs, double[] at)
    {
        int gvar = f.Table("gvar");
        int axes = f.U16(gvar + 4), sharedAt = gvar + (int)f.U32(gvar + 8);
        int count = Math.Min(f.U16(gvar + 12), glyphs.Length);
        bool longOffsets = (f.U16(gvar + 14) & 1) != 0;
        int data = gvar + (int)f.U32(gvar + 16);
        int Off(int g) => longOffsets ? (int)f.U32(gvar + 20 + g * 4) : f.U16(gvar + 20 + g * 2) * 2;
        var peak = new double[axes];
        var start = new double[axes];
        var end = new double[axes];
        for (int g = 0; g < count; g++)
        {
            int from = Off(g), to = Off(g + 1);
            if (to <= from) continue;
            Glyph gl = glyphs[g];
            int gv = data + from;
            int header = f.U16(gv);
            int tuples = header & CountMask;
            int serial = gv + f.U16(gv + 2);
            int[]? sharedPoints = null;
            if ((header & SharedPoints) != 0) sharedPoints = Points(f, ref serial);
            int h = gv + 4;
            for (int t = 0; t < tuples; t++)
            {
                int size = f.U16(h), index = f.U16(h + 2);
                h += 4;
                if ((index & EmbeddedPeak) != 0) { for (int a = 0; a < axes; a++) peak[a] = f.S16(h + a * 2) / F2Dot14; h += axes * 2; }
                else for (int a = 0; a < axes; a++) peak[a] = f.S16(sharedAt + ((index & TupleIndexMask) * axes + a) * 2) / F2Dot14;
                bool inter = (index & Intermediate) != 0;
                if (inter)
                {
                    for (int a = 0; a < axes; a++) { start[a] = f.S16(h + a * 2) / F2Dot14; end[a] = f.S16(h + (axes + a) * 2) / F2Dot14; }
                    h += axes * 4;
                }
                double scalar = Scalar(at, peak, inter ? start : null, inter ? end : null);
                int next = serial + size;
                if (scalar != 0)
                {
                    int p = serial;
                    int[]? points = (index & PrivatePoints) != 0 ? Points(f, ref p) : sharedPoints;
                    int n = points?.Length ?? gl.X.Length;
                    double[] dx = Deltas(f, ref p, n), dy = Deltas(f, ref p, n);
                    Apply(gl, points, dx, dy, scalar);
                }
                serial = next;
            }
        }
    }

    /// <summary>How much of a tuple applies at <paramref name="at"/>: 1 at its peak, falling to 0 at its region's edges.</summary>
    private static double Scalar(double[] at, double[] peak, double[]? start, double[]? end)
    {
        double s = 1;
        for (int a = 0; a < at.Length; a++)
        {
            double p = peak[a], c = at[a];
            if (p == 0 || c == p) continue;
            double lo = start?[a] ?? Math.Min(0, p), hi = end?[a] ?? Math.Max(0, p);
            if (c <= lo || c >= hi) return 0;
            s *= c < p ? (c - lo) / (p - lo) : (hi - c) / (hi - p);
        }
        return s;
    }

    /// <summary>Adds one tuple's deltas; a tuple naming only some points of a simple glyph moves the rest of each contour
    /// by interpolating between its moved neighbours, as the font format defines.</summary>
    private static void Apply(Glyph gl, int[]? points, double[] dx, double[] dy, double scalar)
    {
        int n = gl.X.Length;
        if (points == null)
        {
            for (int i = 0; i < n && i < dx.Length; i++) { gl.Dx[i] += dx[i] * scalar; gl.Dy[i] += dy[i] * scalar; }
            return;
        }
        var tx = new double[n]; var ty = new double[n]; var touched = new bool[n];
        for (int k = 0; k < points.Length; k++)
        {
            int i = points[k];
            if (i < 0 || i >= n) continue;
            tx[i] = dx[k]; ty[i] = dy[k]; touched[i] = true;
        }
        if (gl.Parts == null && gl.Contours > 0)
            for (int c = 0, first = 0; c < gl.Contours; first = gl.Ends[c] + 1, c++)
            {
                Interpolate(gl.X, tx, touched, first, gl.Ends[c]);
                Interpolate(gl.Y, ty, touched, first, gl.Ends[c]);
            }
        for (int i = 0; i < n; i++) { gl.Dx[i] += tx[i] * scalar; gl.Dy[i] += ty[i] * scalar; }
    }

    /// <summary>One axis of one contour: each untouched point takes the delta its two touched neighbours imply.</summary>
    private static void Interpolate(double[] pos, double[] d, bool[] touched, int first, int last)
    {
        var ref_ = new List<int>();
        for (int i = first; i <= last; i++) if (touched[i]) ref_.Add(i);
        if (ref_.Count == 0 || ref_.Count == last - first + 1) return;
        for (int r = 0; r < ref_.Count; r++)
        {
            int a = ref_[r], b = ref_[(r + 1) % ref_.Count];
            for (int i = a + 1; ; i++)
            {
                if (i > last) i = first;
                if (i == b) break;
                double ca = pos[a], cb = pos[b], c = pos[i];
                if (ca == cb) d[i] = d[a] == d[b] ? d[a] : 0;
                else
                {
                    (double loC, double loD, double hiC, double hiD) = ca < cb ? (ca, d[a], cb, d[b]) : (cb, d[b], ca, d[a]);
                    d[i] = c <= loC ? loD : c >= hiC ? hiD : loD + (c - loC) * (hiD - loD) / (hiC - loC);
                }
            }
        }
    }

    /// <summary>gvar's packed point numbers at <paramref name="p"/>; null means every point.</summary>
    private static int[]? Points(Reader f, ref int p)
    {
        int count = f.B(p++);
        if (count == 0) return null;
        if ((count & 0x80) != 0) count = ((count & 0x7F) << 8) | f.B(p++);
        var pts = new int[count];
        int v = 0;
        for (int i = 0; i < count;)
        {
            int control = f.B(p++);
            bool words = (control & 0x80) != 0;
            for (int r = (control & 0x7F) + 1; r > 0 && i < count; r--)
            {
                v += words ? f.U16(p) : f.B(p);
                p += words ? 2 : 1;
                pts[i++] = v;
            }
        }
        return pts;
    }

    /// <summary>gvar's packed deltas at <paramref name="p"/>: <paramref name="n"/> of them.</summary>
    private static double[] Deltas(Reader f, ref int p, int n)
    {
        var d = new double[n];
        for (int i = 0; i < n;)
        {
            int control = f.B(p++);
            int run = (control & 0x3F) + 1;
            for (; run > 0 && i < n; run--)
            {
                if ((control & 0x80) != 0) d[i++] = 0;
                else if ((control & 0x40) != 0) { d[i++] = f.S16(p); p += 2; }
                else d[i++] = (sbyte)f.B(p++);
            }
        }
        return d;
    }

    /// <summary>The static font: the original's tables less <see cref="Dropped"/>, with new <c>glyf</c>, <c>loca</c>
    /// (long), <c>hmtx</c> (a metric per glyph), and <c>head</c> and <c>hhea</c> patched to match.</summary>
    private static byte[] Write(Reader f, Glyph[] glyphs)
    {
        var glyf = new MemoryStream();
        var loca = new byte[(glyphs.Length + 1) * 4];
        var hmtx = new byte[glyphs.Length * 4];
        var boxes = new (int X0, int Y0, int X1, int Y1)?[glyphs.Length];
        int maxAdv = 0;
        for (int g = 0; g < glyphs.Length; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Length);
            Glyph gl = glyphs[g];
            int n = gl.X.Length;
            double left = gl.X[n - Phantoms] + gl.Dx[n - Phantoms], right = gl.X[n - Phantoms + 1] + gl.Dx[n - Phantoms + 1];
            int adv = Math.Max(0, Round(right - left));
            (int X0, int Y0, int X1, int Y1)? box = gl.Empty ? null : Box(glyphs, g, boxes, 0);
            if (!gl.Empty) WriteGlyph(glyf, gl, box!.Value);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), (ushort)adv);
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(g * 4 + 2), (short)(box is { } b ? Round(b.X0 - left) : 0));
            maxAdv = Math.Max(maxAdv, adv);
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyphs.Length * 4), (uint)glyf.Length);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string tag, int off, int len) in f.All())
            if (Array.IndexOf(Dropped, tag) < 0) tables[tag] = f.Slice(off, len);
        tables["glyf"] = glyf.ToArray();
        tables["loca"] = loca;
        tables["hmtx"] = hmtx;
        byte[] head = tables["head"], hhea = tables["hhea"];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(10), (ushort)maxAdv);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphs.Length);
        return Pack(tables);
    }

    /// <summary>Glyph <paramref name="g"/>'s new bounding box; a composite's from its parts', each moved by its offset.</summary>
    private static (int X0, int Y0, int X1, int Y1)? Box(Glyph[] glyphs, int g, (int, int, int, int)?[] memo, int depth)
    {
        if (memo[g] is { } m) return m;
        Glyph gl = glyphs[g];
        if (gl.Empty || depth > 8) return null;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        if (gl.Parts == null)
            for (int i = 0; i < gl.Points; i++)
            {
                int x = Round(gl.X[i] + gl.Dx[i]), y = Round(gl.Y[i] + gl.Dy[i]);
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
        else
            for (int k = 0; k < gl.Parts.Count; k++)
            {
                if (gl.Parts[k].Glyph >= glyphs.Length || Box(glyphs, gl.Parts[k].Glyph, memo, depth + 1) is not var (a0, b0, a1, b1)) continue;
                int dx = Round(gl.X[k] + gl.Dx[k]), dy = Round(gl.Y[k] + gl.Dy[k]);
                x0 = Math.Min(x0, a0 + dx); y0 = Math.Min(y0, b0 + dy); x1 = Math.Max(x1, a1 + dx); y1 = Math.Max(y1, b1 + dy);
            }
        if (x0 > x1) return null;
        var box = ((int)x0, (int)y0, (int)x1, (int)y1);
        memo[g] = box;
        return box;
    }

    /// <summary>One glyph's record: a simple glyph's points as plain words with no hinting, or a composite's parts with
    /// word offsets.</summary>
    private static void WriteGlyph(MemoryStream o, Glyph gl, (int X0, int Y0, int X1, int Y1) box)
    {
        void W16(int v) { o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }
        W16(gl.Contours); W16(box.X0); W16(box.Y0); W16(box.X1); W16(box.Y1);
        if (gl.Parts != null)
        {
            for (int k = 0; k < gl.Parts.Count; k++)
            {
                (int flags, int glyph, int a1, int a2, byte[] t) = gl.Parts[k];
                bool xy = (flags & ArgsXy) != 0;
                W16((flags | ArgWords) & ~Instructions);
                W16(glyph);
                W16(xy ? Round(gl.X[k] + gl.Dx[k]) : a1);
                W16(xy ? Round(gl.Y[k] + gl.Dy[k]) : a2);
                o.Write(t);
            }
            return;
        }
        foreach (int e in gl.Ends) W16(e);
        W16(0);
        int count = gl.Points;
        for (int i = 0; i < count; i++) o.WriteByte((byte)(gl.On[i] ? 1 : 0));
        int prev = 0;
        for (int i = 0; i < count; i++) { int x = Round(gl.X[i] + gl.Dx[i]); W16(x - prev); prev = x; }
        prev = 0;
        for (int i = 0; i < count; i++) { int y = Round(gl.Y[i] + gl.Dy[i]); W16(y - prev); prev = y; }
    }

    /// <summary>The font file: an sfnt header, the table records sorted by tag, and each table padded to four bytes.</summary>
    private static byte[] Pack(SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count, entry = 1, log = 0;
        while (entry * 2 <= n) { entry *= 2; log++; }
        int at = 12 + n * 16;
        int total = at + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var o = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(o, 0x00010000);   // not-an-offset: the TrueType sfnt version
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(6), (ushort)(entry * 16));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(10), (ushort)(n * 16 - entry * 16));
        int rec = 12;
        foreach ((string tag, byte[] t) in tables)
        {
            for (int c = 0; c < 4; c++) o[rec + c] = (byte)tag[c];
            t.CopyTo(o, at);
            uint sum = 0;
            for (int i = 0; i < t.Length; i += 4)
            {
                uint word = 0;
                for (int b = 0; b < 4; b++) word = (word << 8) | (i + b < t.Length ? t[i + b] : 0u);
                sum += word;
            }
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(rec + 4), sum);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(rec + 8), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(rec + 12), (uint)t.Length);
            rec += 16;
            at += (t.Length + 3) & ~3;
        }
        return o;
    }

    /// <summary>Rounds half up, as fontTools' <c>otRound</c> does, so a coordinate lands where its instancer puts it.</summary>
    private static int Round(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>Big-endian reads over one font's tables, by tag, at absolute offsets into the file.</summary>
    private sealed class Reader
    {
        private readonly byte[] _b;
        private readonly Dictionary<string, (int Off, int Len)> _tables = new(StringComparer.Ordinal);

        public Reader(byte[] b, int at)
        {
            _b = b;
            int n = U16(at + 4);
            for (int i = 0; i < n; i++)
            {
                int rec = at + 12 + i * 16;
                _tables[Tag(rec)] = ((int)U32(rec + 8), (int)U32(rec + 12));
            }
        }

        public bool Has(string tag) => _tables.ContainsKey(tag);
        public int Table(string tag) => _tables[tag].Off;
        public IEnumerable<(string Tag, int Off, int Len)> All() => _tables.Select(t => (t.Key, t.Value.Off, t.Value.Len));
        public string Tag(int at) => System.Text.Encoding.ASCII.GetString(_b, at, 4);
        public byte B(int at) => _b[at];
        public int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_b.AsSpan(at));
        public int S16(int at) => BinaryPrimitives.ReadInt16BigEndian(_b.AsSpan(at));
        public uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_b.AsSpan(at));
        public int S32(int at) => BinaryPrimitives.ReadInt32BigEndian(_b.AsSpan(at));
        public byte[] Slice(int at, int len) => _b.AsSpan(at, len).ToArray();
    }
}

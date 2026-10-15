using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Sorts each cell of a 2x2 grass atlas into BLADE (procedural blades can stand in for it) or
/// CARD (keep the alpha-tested card), biased toward CARD -- a flower drawn as blades is visible, grass drawn
/// as a card is not. A port of <c>tools/grass/classify.py</c>; its thresholds and tests are that file's.
/// A BLADE cell also yields its root/mid/tip colour ramp (display space), what a blade is tinted with.</summary>
internal static unsafe class GrassAtlas
{
    internal struct Cell
    {
        public bool Blade;
        public Vector3 Root, Mid, Tip;
        /// <summary>The art's mean blade length as a share of the card height: each covered column's top,
        /// averaged. A card's tallest blade reaches its top; most of the tuft does not.</summary>
        public float Height;
    }

    /// <summary>Alpha a texel must clear to count as blade (the prototype's <c>CUT</c>).</summary>
    private const int Cut = 127;
    /// <summary>The prototype's four verdict thresholds: share of foreign-coloured texels, share of tip-side
    /// texels surviving an erosion a blade is too thin for, mean opaque run width over the cell width, and
    /// share of rows carrying any coverage.</summary>
    private const float ForeignMax = 0.03f, BlobMax = 0.04f, RunMax = 0.05f, ExtentMin = 0.35f;
    /// <summary>Hue window (degrees) that counts as grass: straw yellow through green.</summary>
    private const float HueLo = 35f, HueHi = 175f;

    /// <summary>Classify the four cells of a tightly packed RGBA8 atlas (<paramref name="w"/> x
    /// <paramref name="h"/>, GL row order). Cold: one call per atlas upload; allocates a mask per cell.</summary>
    internal static void Classify(byte* rgba, int w, int h, Span<Cell> cells)
    {
        int cw = w / 2, ch = h / 2;
        for (int i = 0; i < 4; i++)
            cells[i] = cw < 4 || ch < 4 ? default : Measure(rgba, w, (i & 1) * cw, (i >> 1) * ch, cw, ch);
    }

    private static Cell Measure(byte* rgba, int stride, int x0, int y0, int w, int h)
    {
        ref TexelRec Px(int x, int y) => ref ((TexelRec*)rgba)[(y0 + y) * stride + x0 + x];
        int edge = Math.Max(1, h / 10), lo = 0, hi = 0;
        for (int y = 0; y < edge; y++)
            for (int x = 0; x < w; x++) { lo += Px(x, y).A > Cut ? 1 : 0; hi += Px(x, h - 1 - y).A > Cut ? 1 : 0; }
        bool flip = hi > lo;
        // m[y*w+x] in ROOT-FIRST order
        var m = new bool[w * h];
        int n = 0, rows = 0, runs = 0, runSum = 0, foreign = 0;
        var top = new int[w];
        Span<Vector3> band = stackalloc Vector3[3];
        Span<float> bandW = stackalloc float[3];
        for (int y = 0; y < h; y++)
        {
            int sy = flip ? h - 1 - y : y, run = 0;
            bool any = false;
            for (int x = 0; x < w; x++)
            {
                ref TexelRec p = ref Px(x, sy);
                bool on = p.A > Cut;
                m[y * w + x] = on;
                if (on)
                {
                    n++; any = true; run++;
                    top[x] = y + 1;
                    var c = new Vector3(p.R, p.G, p.B) / 255f;
                    if (Foreign(c)) foreign++;
                    int k = Math.Min(y * 3 / h, 2);
                    float a = p.A / 255f;
                    band[k] += c * a; bandW[k] += a;
                }
                if ((!on || x == w - 1) && run > 0) { runs++; runSum += run; run = 0; }
            }
            if (any) rows++;
        }
        if (n == 0) return default;
        float extent = (float)rows / h, runW = (float)runSum / runs / w, foreignShare = (float)foreign / n;
        float blob = Blob(m, w, h, Math.Max(1, w / 64));
        long topSum = 0; int cols = 0;
        for (int x = 0; x < w; x++) if (top[x] > 0) { topSum += top[x]; cols++; }
        var cell = new Cell
        {
            Blade = foreignShare <= ForeignMax && blob <= BlobMax && runW <= RunMax && extent >= ExtentMin,
            Root = bandW[0] > 0f ? band[0] / bandW[0] : Vector3.Zero,
            Mid = bandW[1] > 0f ? band[1] / bandW[1] : Vector3.Zero,
            Tip = bandW[2] > 0f ? band[2] / bandW[2] : Vector3.Zero,
            Height = (float)topSum / cols / h,
        };
        // a band the art leaves empty borrows its neighbour, so a short blade's ramp does not end in black
        if (bandW[1] <= 0f) cell.Mid = cell.Root;
        if (bandW[2] <= 0f) cell.Tip = cell.Mid;
        return cell;
    }

    /// <summary>A saturated non-grass hue, or near-white: a petal, not a blade.</summary>
    private static bool Foreign(Vector3 c)
    {
        float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float sat = max > 0f ? (max - min) / max : 0f;
        if (max > 0.85f && sat < 0.2f) return true;
        if (sat <= 0.35f) return false;
        float d = max - min, hue;
        if (max == c.X) hue = 60f * (((c.Y - c.Z) / d + 6f) % 6f);
        else if (max == c.Y) hue = 60f * ((c.Z - c.X) / d + 2f);
        else hue = 60f * ((c.X - c.Y) / d + 4f);
        return hue < HueLo || hue > HueHi;
    }

    /// <summary>Share of the tip side's (above 30% of the height) opaque texels that survive a box erosion of
    /// radius <paramref name="r"/>: a blob does, a blade a few texels wide does not. Separable, out of bounds
    /// counts as empty.</summary>
    private static float Blob(bool[] m, int w, int h, int r)
    {
        int y0 = (int)(h * 0.3f), th = h - y0, total = 0, kept = 0;
        var row = new bool[w * th];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < w; x++)
            {
                bool all = true;
                for (int d = -r; d <= r && all; d++) all = x + d >= 0 && x + d < w && m[(y0 + y) * w + x + d];
                row[y * w + x] = all;
            }
        for (int y = 0; y < th; y++)
            for (int x = 0; x < w; x++)
            {
                if (m[(y0 + y) * w + x]) total++;
                bool all = true;
                for (int d = -r; d <= r && all; d++) all = y + d >= 0 && y + d < th && row[(y + d) * w + x];
                if (all) kept++;
            }
        return total == 0 ? 0f : (float)kept / total;
    }

    // ---- self-check --------------------------------------------------------------------------------------

    private const int N = 128;

    /// <summary>Thirty thin leaning green blades on transparent black, the prototype's synthetic grass.</summary>
    private static byte[] Blades(uint seed)
    {
        var px = new byte[N * N * 4];
        for (int b = 0; b < 30; b++)
        {
            float x = 10f + GrassField.Rnd(ref seed) * (N - 20), lean = (GrassField.Rnd(ref seed) - 0.5f) * 0.6f;
            int top = (int)((0.5f + GrassField.Rnd(ref seed) * 0.45f) * N);
            for (int y = 0; y < top; y++)
            {
                int cx = (int)(x + lean * y);
                for (int k = 0; k < 2; k++)
                    if (cx + k >= 0 && cx + k < N) Put(px, cx + k, y, 64, (byte)(115 + 51 * y / N), 25);
            }
        }
        return px;
    }

    private static void Put(byte[] px, int x, int y, byte r, byte g, byte b)
    {
        int o = (y * N + x) * 4;
        px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = 255;
    }

    private static void Discs(byte[] px, byte r, byte g, byte b)
    {
        foreach ((int cx, int cy) in new[] { (30, 100), (70, 110), (100, 95) })
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
                if ((y - cy) * (y - cy) + (x - cx) * (x - cx) < 36) Put(px, x, y, r, g, b);
    }

    private static byte[] FlipRows(byte[] px)
    {
        var o = new byte[px.Length];
        for (int y = 0; y < N; y++) Array.Copy(px, y * N * 4, o, (N - 1 - y) * N * 4, N * 4);
        return o;
    }

    /// <summary>One synthetic cell tiled into all four atlas cells and classified; the shared verdict, or a
    /// CARD default when the four cells disagree.</summary>
    private static Cell Tiled(byte[] cell)
    {
        var atlas = new byte[4 * N * N * 4];
        for (int c = 0; c < 4; c++)
            for (int y = 0; y < N; y++)
                Array.Copy(cell, y * N * 4, atlas, (((c >> 1) * N + y) * 2 * N + (c & 1) * N) * 4, N * 4);
        Span<Cell> cells = stackalloc Cell[4];
        fixed (byte* p = atlas) Classify(p, 2 * N, 2 * N, cells);
        bool same = true;
        for (int i = 1; i < 4; i++) same &= cells[i].Blade == cells[0].Blade;
        return same ? cells[0] : default;
    }

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        byte[] grass = Blades(1u);
        Cell g = Tiled(grass);
        check.Expect(g.Blade, "thin green blades classify BLADE, in every cell of the atlas");
        check.Expect(g.Tip.Y > g.Root.Y && g.Root.Y > 0.3f && g.Root.X < g.Root.Y, "and carry a green ramp that brightens toward the tip");
        check.Expect(Tiled(FlipRows(grass)).Blade, "an atlas stored tip-down still classifies BLADE (the root is found, not assumed)");
        check.Expect(g.Height > 0.5f && g.Height < 0.95f && MathF.Abs(Tiled(FlipRows(grass)).Height - g.Height) < 0.02f,
          "the blade length is the art's mean blade top (blades span 0.5..0.95), read root-first either way up");

        byte[] flowers = Blades(1u);
        Discs(flowers, 217, 38, 77);
        check.Expect(!Tiled(flowers).Blade, "saturated flower heads over the blades classify CARD");
        byte[] white = Blades(1u);
        Discs(white, 242, 242, 230);
        check.Expect(!Tiled(white).Blade, "white flower heads do too -- the colour test is not the only guard");
        check.Expect(!Tiled(new byte[N * N * 4]).Blade, "an empty cell is CARD, never an invisible blade field");

        byte[] slab = new byte[N * N * 4];
        for (int y = 0; y < N; y++) for (int x = 20; x < 100; x++) Put(slab, x, y, 60, 120, 30);
        check.Expect(!Tiled(slab).Blade, "a wide opaque green card (a bush) classifies CARD");
        log.Info(check.Ok ? "GrassAtlas.SelfCheck: passed" : "GrassAtlas.SelfCheck: FAILED");
    }
}

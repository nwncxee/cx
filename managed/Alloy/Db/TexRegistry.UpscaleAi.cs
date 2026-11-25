using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>
/// alloy.upscaleai: an upscale's colour through Real-ESRGAN's compact general model on DirectML (native/ai/cxai.cpp):
/// four times up in fixed tiles, boxed back to two. Alpha keeps <see cref="Upscale2x"/>'s. Where the runtime, the model
/// or a D3D12 GPU is missing it stands down once, said in cx.log, and the background rebuild takes Upscale2x whole.
/// </summary>
internal static partial class TexRegistry
{
    [DllImport("cxai", CharSet = CharSet.Unicode)] private static extern int cxai_open(int slot, string dir, string model);
    [DllImport("cxai")] private static extern void cxai_close(int slot);
    [DllImport("cxai")] private static extern int cxai_run(int slot, float[] input, int c, int w, int h, float[] output, int outCount);
    /// <summary>The upscaler's session slot in cxai; the normal model has its own.</summary>
    private const int AiSlotUpscale = 0;

    /// <summary>The model files beside version.dll, by alloy.upscalemodel: the compact general model and the full x4plus.</summary>
    private static readonly string[] AiModelFiles = ["cx_upscale.onnx", "cx_upscale_full.onnx"];
    private static readonly string[] AiModelNames = ["Real-ESRGAN general x4 (compact)", "Real-ESRGAN x4plus (full)"];
    /// <summary>Source texels a tile covers, and the margin read around it so its edge has context: one tile size,
    /// so DirectML compiles the graph once.</summary>
    private const int AiTile = 128, AiPad = 8, AiSpan = AiTile + 2 * AiPad;
    /// <summary>The model's own scale.</summary>
    private const int AiScale = 4;

    /// <summary>Whether a model is up and which: untried, up, refused for this model, or refused for the session.</summary>
    private sealed class AiState
    {
        public int State;
        public int Model = -1;
        public string? Why;
    }

    private static readonly AiState _ai = new();
    private const int AiUntried = 0, AiUp = 1, AiRefused = -1, AiModelRefused = -2;

    /// <summary>The model alloy.upscalemodel asks for: 0 compact, 1 full.</summary>
    private static int AiModelWanted => Math.Clamp(Renderer.UpscaleModel.V, 0, AiModelFiles.Length - 1);

    /// <summary>Is the AI rebuild wanted and not refused for the model asked? Read by the cache key before it is tried.</summary>
    private static bool AiWanted
        => Renderer.UpscaleAi.V && _ai.State != AiRefused && !(_ai.State == AiModelRefused && _ai.Model == AiModelWanted);

    /// <summary>The model asked for, opened on first use from a worker and reopened when the choice moves; false (said
    /// once) when it cannot be. Caller holds <see cref="_ai"/>.</summary>
    private static bool AiReady()
    {
        int want = AiModelWanted;
        if (_ai.State == AiUp && _ai.Model == want) return true;
        if (_ai.State == AiRefused || _ai.State == AiModelRefused && _ai.Model == want) return false;
        if (!OperatingSystem.IsWindows()) return Refuse("the AI upscale is Windows-only in this build");
        string dir = VulkanToolchain.InstallFolder;
        string model = System.IO.Path.Combine(dir, AiModelFiles[want]);
        _ai.Model = want;
        if (!System.IO.File.Exists(model)) return Refuse($"{AiModelFiles[want]} is not beside version.dll", model: true);
        int e;
        try
        {
            if (_ai.State == AiUp) cxai_close(AiSlotUpscale);
            e = cxai_open(AiSlotUpscale, dir, model);
        }
        catch (Exception x) when (x is DllNotFoundException or EntryPointNotFoundException)
        { return Refuse("this build was made without the AI runtime"); }
        if (e != 0)
            return Refuse(AiOpenWhy(e, AiModelFiles[want]), model: e == 7);
        _ai.State = AiUp;
        _log?.Always($"TexRegistry: the AI upscale is up ({AiModelNames[want]} on DirectML)");
        return true;
    }

    /// <summary>Stand the AI rebuild down for the session, saying why once. Caller holds <see cref="_ai"/>.</summary>
    private static bool Refuse(string why, bool model = false)
    {
        _ai.State = model ? AiModelRefused : AiRefused; _ai.Why = why;
        _log?.Always($"TexRegistry: the AI upscale is OFF -- {why}. Textures upscale the classic way.");
        return false;
    }

    /// <summary>The largest side a texture may have and still take the model's full 4x: 512 px becomes 2048.</summary>
    private const int Ai4xMaxSide = 512;

    /// <summary>The scale an upscale of a <paramref name="w"/> x <paramref name="h"/> texture takes: the model's own 4x where
    /// alloy.upscalefactor asks it, the AI is in play and the result stays inside 2048 px; else 2x.</summary>
    private static int UpscaleFactor(int w, int h, bool ai)
        => ai && Renderer.UpscaleFactor.V == AiScale && Math.Max(w, h) <= Ai4xMaxSide ? AiScale : 2;

    /// <summary>RGBA8 <paramref name="src"/> (<paramref name="w"/> x <paramref name="h"/>) at <paramref name="factor"/> (2 or
    /// 4) times its size, colour from the model. At 2x alpha is <paramref name="classic"/>'s (Upscale2x's answer); at 4x it is
    /// bilinear from the source, kept binary for a cutout. Null where the model is not up.</summary>
    private static byte[]? AiUpscale(byte[] src, int w, int h, bool wrap, byte[] classic, int factor, bool hard)
    {
        if (!Renderer.UpscaleAi.V) return null;
        // one texture at a time, whole: a model switch never lands between two of its tiles
        lock (_ai)
        {
            if (!AiReady()) return null;
            return AiRun(src, w, h, wrap, classic, factor, hard);
        }
    }

    /// <summary><see cref="AiUpscale"/>'s tiles through the open model. Caller holds <see cref="_ai"/>.</summary>
    private static byte[]? AiRun(byte[] src, int w, int h, bool wrap, byte[] classic, int factor, bool hard)
    {
        int wf = w * factor, outSpan = AiSpan * AiScale, step = AiScale / factor;
        float[] input = new float[3 * AiSpan * AiSpan], output = new float[3 * outSpan * outSpan];
        byte[] dst = factor == 2 ? (byte[])classic.Clone() : AlphaUp(src, w, h, factor, wrap, hard);
        float norm = 1f / (step * step);
        float[] detail = DetailMap(src, w, h, wrap);
        float protect = UpscaleProtect;
        for (int ty = 0; ty < h; ty += AiTile)
            for (int tx = 0; tx < w; tx += AiTile)
            {
                // the tile and its margin, edges read as the texture's own sampler would (wrap or clamp)
                for (int y = 0; y < AiSpan; y++)
                {
                    int sy = Edge(ty - AiPad + y, h, wrap);
                    for (int x = 0; x < AiSpan; x++)
                    {
                        int sx = Edge(tx - AiPad + x, w, wrap);
                        for (int c = 0; c < 3; c++) input[Plane(c, x, y, AiSpan)] = src[At(sx, sy, w, c)] / 255f;
                    }
                }
                if (cxai_run(AiSlotUpscale, input, 3, AiSpan, AiSpan, output, output.Length) != 0)
                {
                    Refuse("a tile did not run");
                    return null;
                }
                // each output texel is the box of the model's texels it covers (one at 4x, four at 2x), past the margin
                int ow = Math.Min(AiTile, w - tx) * factor, oh = Math.Min(AiTile, h - ty) * factor;
                for (int y = 0; y < oh; y++)
                    for (int x = 0; x < ow; x++)
                    {
                        int px = AiPad * AiScale + x * step, py = AiPad * AiScale + y * step;
                        int ox = tx * factor + x, oy = ty * factor + y;
                        int to = At(ox, oy, wf, 0);
                        // the classic answer under this texel (the 2x image, read at this scale) and how much detail the
                        // source has here: a smooth gradient keeps the classic answer, so the model invents no ripples in it
                        int from = At(ox * 2 / factor, oy * 2 / factor, w * 2, 0);
                        float k = 1f - protect * (1f - DetailAt(detail, w, h, (ox + 0.5f) / factor - 0.5f, (oy + 0.5f) / factor - 0.5f, wrap));
                        for (int c = 0; c < 3; c++)
                        {
                            float v = 0f;
                            for (int dy = 0; dy < step; dy++)
                                for (int dx = 0; dx < step; dx++) v += output[Plane(c, px + dx, py + dy, outSpan)];
                            float ai = v * norm * 255f;
                            dst[to + c] = (byte)Math.Clamp(classic[from + c] + (ai - classic[from + c]) * k + 0.5f, 0f, 255f);
                        }
                    }
            }
        return dst;
    }

    /// <summary>alloy.upscaleprotect, in tenths: how far smooth areas keep the classic answer (0 = the AI everywhere).</summary>
    private static float UpscaleProtect => Math.Clamp(Renderer.UpscaleProtect.V, 0f, 1f);

    /// <summary>Local luminance spread (0..255) under which the AI's answer gives way wholly to the classic one, and over
    /// which it is taken whole: a flat gradient is where a model invents texture, real detail is where it earns its keep.</summary>
    private const float AiFlatBelow = 1.5f, AiDetailAbove = 5f;
    /// <summary>The spread is read over a (2r+1)-square window: 5x5 tells a soft but patterned texture from a flat gradient.</summary>
    private const int AiDetailRadius = 2;

    /// <summary>How much of the AI answer each source texel takes, 0..1: the smoothstep of its 5x5 luminance spread
    /// between <see cref="AiFlatBelow"/> and <see cref="AiDetailAbove"/>.</summary>
    private static float[] DetailMap(byte[] src, int w, int h, bool wrap)
    {
        float[] d = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0f, sq = 0f;
                for (int dy = -AiDetailRadius; dy <= AiDetailRadius; dy++)
                    for (int dx = -AiDetailRadius; dx <= AiDetailRadius; dx++)
                    {
                        int o = At(Edge(x + dx, w, wrap), Edge(y + dy, h, wrap), w, 0);
                        float l = 0.2126f * src[o] + 0.7152f * src[o + 1] + 0.0722f * src[o + 2];
                        sum += l; sq += l * l;
                    }
                const float n = (2 * AiDetailRadius + 1) * (2 * AiDetailRadius + 1);
                float mean = sum / n, sd = MathF.Sqrt(MathF.Max(sq / n - mean * mean, 0f));
                float t = Math.Clamp((sd - AiFlatBelow) / (AiDetailAbove - AiFlatBelow), 0f, 1f);
                d[At(x, y, w, 0) / 4] = t * t * (3f - 2f * t);
            }
        return d;
    }

    /// <summary><see cref="DetailMap"/> read bilinear at source position (<paramref name="fx"/>, <paramref name="fy"/>), so a
    /// blend's edge does not step texel by texel at 4x.</summary>
    private static float DetailAt(float[] d, int w, int h, float fx, float fy, bool wrap)
    {
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ax = fx - x0, ay = fy - y0;
        int xa = Edge(x0, w, wrap), xb = Edge(x0 + 1, w, wrap), ya = Edge(y0, h, wrap), yb = Edge(y0 + 1, h, wrap);
        float top = d[At(xa, ya, w, 0) / 4] * (1f - ax) + d[At(xb, ya, w, 0) / 4] * ax;
        float bot = d[At(xa, yb, w, 0) / 4] * (1f - ax) + d[At(xb, yb, w, 0) / 4] * ax;
        return top * (1f - ay) + bot * ay;
    }

    /// <summary>An RGBA8 image <paramref name="factor"/> times <paramref name="src"/>'s size whose alpha is <paramref name="src"/>'s,
    /// bilinear (binary at 128 for a <paramref name="hard"/> cutout); colour left for the model to fill.</summary>
    private static byte[] AlphaUp(byte[] src, int w, int h, int factor, bool wrap, bool hard)
    {
        int wf = w * factor, hf = h * factor;
        byte[] d = new byte[wf * hf * 4];
        for (int y = 0; y < hf; y++)
        {
            float fy = (y + 0.5f) / factor - 0.5f;
            int y0 = (int)MathF.Floor(fy);
            float ay = fy - y0;
            int ya = Edge(y0, h, wrap), yb = Edge(y0 + 1, h, wrap);
            for (int x = 0; x < wf; x++)
            {
                float fx = (x + 0.5f) / factor - 0.5f;
                int x0 = (int)MathF.Floor(fx);
                float ax = fx - x0;
                int xa = Edge(x0, w, wrap), xb = Edge(x0 + 1, w, wrap);
                float top = src[At(xa, ya, w, 3)] * (1f - ax) + src[At(xb, ya, w, 3)] * ax;
                float bot = src[At(xa, yb, w, 3)] * (1f - ax) + src[At(xb, yb, w, 3)] * ax;
                float a = top * (1f - ay) + bot * ay;
                d[At(x, y, wf, 3)] = hard ? (a >= UpscaleAlphaIn ? (byte)255 : (byte)0) : (byte)Math.Clamp(a + 0.5f, 0f, 255f);
            }
        }
        return d;
    }

    /// <summary>Element (<paramref name="x"/>, <paramref name="y"/>) of channel <paramref name="c"/> in a planar
    /// 3 x <paramref name="span"/> x <paramref name="span"/> tensor.</summary>
    private static int Plane(int c, int x, int y, int span) => (c * span + y) * span + x;
}

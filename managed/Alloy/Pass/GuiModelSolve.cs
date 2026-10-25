using System;
using CX.Framework;
using CX.Graphics;
using NuiModelDoll = CX.Services.Nui.NuiModelDoll;

namespace CX.Alloy;

/// <summary>
/// A model view on a see-through background, drawn so an effect that only adds light covers what it lights. Additive draws write
/// their square's alpha along with their light, so the doll is drawn twice, over black and over white, into scratch textures, and
/// <c>cx_mvsolve_f.glsl</c> reads each pixel's coverage from how much of the white the black one lacks. A view with no additive
/// draw, or with an opaque background, keeps the single pass.
/// </summary>
internal static unsafe class GuiModelSolve
{
    private sealed class SolveState
    {
        public uint Prog, Vao;
        public int Bg, Clock;
        public bool Tried, Said;
        public readonly Pair[] Pairs = new Pair[NuiModelDoll.MaxViews];
    }

    /// <summary>The two scratch textures for one view size, and when they were last asked for.</summary>
    private struct Pair { public uint Black, White; public int W, H, Used; }

    private static SolveState _s = new();

    /// <summary>Does the run of layers drawing into one model texture, starting at <paramref name="first"/>, need the two-pass solve?
    /// <paramref name="end"/> is the layer after the run (or <paramref name="to"/>).</summary>
    internal static bool Wanted(int first, int to, NuiModelDoll.ModelSlot? slot, out int end)
        => Wanted(first, to, slot, true, out end);

    /// <inheritdoc cref="Wanted(int, int, NuiModelDoll.ModelSlot?, out int)"/>
    /// <param name="glProgram">Ask whether the GL solve is built; a caller on another backend has its own.</param>
    internal static bool Wanted(int first, int to, NuiModelDoll.ModelSlot? slot, bool glProgram, out int end)
    {
        uint target = GuiCapture.Layers[first].Target;
        end = first;
        bool additive = false;
        while (end < to && GuiCapture.Layers[end].Target == target)
        {
            ref GuiCapture.Layer l = ref GuiCapture.Layers[end];
            for (int di = l.First; !additive && di < l.First + l.Count; di++)
                additive = IsAdditive(in GuiCapture.Draws[di]);
            end++;
        }
        if (slot == null) return false;
        float* c = stackalloc float[4];
        BackgroundOf(slot, c);
        return additive && c[3] < 1f && (!glProgram || Ready());
    }

    /// <summary>Writes the slot's background colour into <paramref name="c"/> (4 floats), or leaves it as it was for no slot.</summary>
    private static void BackgroundOf(NuiModelDoll.ModelSlot? slot, float* c)
    {
        if (slot != null) NuiModelDoll.Background(slot, c);
    }

    /// <summary>Does a draw add its light to what is under it, the way an effect's fire ring does?</summary>
    internal static bool IsAdditive(in GuiCapture.Draw d) => d.Blend && d.Dst == Gl.ONE;

    private static bool Ready()
    {
        SolveState s = _s;
        if (s.Prog != 0) return true;
        if (s.Tried || Gl.BindTextureUnit == null || Gl.CreateVertexArrays == null || Gl.CreateTextures == null || Gl.TextureStorage2D == null) return false;
        s.Tried = true;
        s.Prog = GlProgram.Graphics(ShaderSource.Get("cx_fullscreen_v.glsl"), ShaderSource.Get("cx_mvsolve_f.glsl"), null, "Alloy model view solve");
        if (s.Prog == 0) return false;
        s.Bg = GlProgram.Loc(s.Prog, "uBg");
        uint v; Gl.CreateVertexArrays(1, &v); s.Vao = v;
        return true;
    }

    /// <summary>The scratch texture for pass 0 (over black) or 1 (over white) at this size. Each size keeps its own pair, so views of
    /// different sizes do not remake each other's every frame; with every pair taken, the one asked for longest ago is remade.</summary>
    internal static uint Scratch(int pass, int w, int h)
    {
        SolveState s = _s;
        ref Pair p = ref Pick(s, w, h);
        return pass == 0 ? p.Black : p.White;
    }

    private static ref Pair Pick(SolveState s, int w, int h)
    {
        int old = 0;
        for (int i = 0; i < s.Pairs.Length; i++)
        {
            ref Pair c = ref s.Pairs[i];
            if (c.Black != 0 && c.W == w && c.H == h) { c.Used = ++s.Clock; return ref c; }
            if (c.Used < s.Pairs[old].Used) old = i;
        }
        ref Pair p = ref s.Pairs[old];
        uint t;
        if (p.Black != 0) { t = p.Black; Gl.DeleteTextures(1, &t); t = p.White; Gl.DeleteTextures(1, &t); }
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t); p.Black = t;
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, w, h);
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t); p.White = t;
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, w, h);
        p.W = w; p.H = h; p.Used = ++s.Clock;
        return ref p;
    }

    /// <summary>Writes the solved image, over the server's background, into the framebuffer bound now. GL state the layer loop
    /// caches is the caller's to put back.</summary>
    internal static void Resolve(int w, int h, NuiModelDoll.ModelSlot? slot, ExtenderLog? log)
    {
        SolveState s = _s;
        if (!s.Said) { s.Said = true; log?.Always($"GuiModelSolve: a model view with additive draws is solved over black and white ({w}x{h})"); }
        float* c = stackalloc float[4];
        BackgroundOf(slot, c);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE);
        Gl.Viewport(0, 0, w, h);
        Gl.UseProgram(s.Prog);
        ref Pair pair = ref Pick(s, w, h);
        Gl.BindTextureUnit(0, pair.Black);
        Gl.BindTextureUnit(1, pair.White);
        Gl.Uniform4f(s.Bg, c[0], c[1], c[2], c[3]);
        Gl.BindVertexArray(s.Vao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.Enable(Gl.SCISSOR_TEST);
    }

    /// <summary>GL names die with the context.</summary>
    internal static void ContextLost() => _s = new SolveState();

    /// <summary>Oracle: hand-written draws.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var add = new GuiCapture.Draw { Blend = true, Src = Gl.SRC_ALPHA, Dst = Gl.ONE };
        var over = new GuiCapture.Draw { Blend = true, Src = Gl.SRC_ALPHA, Dst = Gl.ONE_MINUS_SRC_ALPHA };
        var off = new GuiCapture.Draw { Blend = false, Dst = Gl.ONE };
        check.Expect(IsAdditive(in add), "blending onto the destination (src alpha, one) adds light");
        check.Expect(!IsAdditive(in over), "the usual over blend does not");
        check.Expect(!IsAdditive(in off), "a draw with blending off does not, whatever its stale factors");
        log.Info(check.Ok ? "GuiModelSolve.SelfCheck: passed" : "GuiModelSolve.SelfCheck: FAILED");
    }
}

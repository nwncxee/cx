using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Services.Nui;

/// <summary>
/// The model view's see-through background with Alloy off. An effect that adds light writes its square's alpha with its light, so
/// one engine pass over a clear background leaves a lit square. A view that names effects is drawn twice, over black and over
/// white, and the same <c>cx_mvsolve_f.glsl</c> Alloy uses reads each pixel's coverage from how much of the white the black one lacks.
/// </summary>
internal static unsafe partial class NuiModelEngine
{
    private sealed class SolveGl
    {
        public uint Prog, Vao;
        public int Bg;
        public bool Tried, Said;
    }

    private static readonly SolveGl _solve = new();

    /// <summary>The solve's program and the slot's second target and output, at the view's size. False (said once) when the program did not build.</summary>
    private static bool EnsureSolve(ExtenderLog? log, Scratch k, int w, int h)
    {
        SolveGl g = _solve;
        if (g.Prog == 0)
        {
            if (g.Tried || Gl.CreateVertexArrays == null) return false;
            g.Tried = true;
            g.Prog = GlProgram.Graphics(CX.Alloy.ShaderSource.Get("cx_fullscreen_v.glsl"), CX.Alloy.ShaderSource.Get("cx_mvsolve_f.glsl"), null, "model view solve (Alloy off)");
            if (g.Prog == 0) { log?.Always("NuiModelEngine: the solve program did not build -- see-through views with effects draw one pass"); return false; }
            g.Bg = GlProgram.Loc(g.Prog, "uBg");
            uint v; Gl.CreateVertexArrays(1, &v); g.Vao = v;
        }
        if (k.OutColor != 0 && k.SolveW == w && k.SolveH == h) return true;
        if (k.WhiteFbo == 0) { uint f; Gl.GenFramebuffers(1, &f); k.WhiteFbo = f; Gl.GenFramebuffers(1, &f); k.OutFbo = f; }
        if (k.OutColor != 0) { uint* old = stackalloc uint[2]; old[0] = k.WhiteColor; old[1] = k.OutColor; Gl.DeleteTextures(2, old); }
        k.WhiteColor = Colour(w, h);
        k.OutColor = Colour(w, h);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, k.WhiteFbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, k.WhiteColor, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.TEXTURE_2D, k.Depth, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, k.OutFbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, k.OutColor, 0);
        k.SolveW = w; k.SolveH = h;
        if (!g.Said) { g.Said = true; log?.Always($"NuiModelEngine: a see-through view with effects is solved over black and white ({w}x{h}), Alloy is off"); }
        return true;
    }

    /// <summary>Solve the slot's two passes, over <paramref name="bg"/> (4 floats, straight), into its output target. GL state the engine caches is put back.</summary>
    private static void Resolve(Scratch k, float* bg, int w, int h)
    {
        SolveGl g = _solve;
        int prog, vao, active, t0, t1, blend, depth, cull;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prog);
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &vao);
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &active);
        Gl.GetIntegerv(Gl.BLEND, &blend);
        Gl.GetIntegerv(Gl.DEPTH_TEST, &depth);
        Gl.GetIntegerv(Gl.CULL_FACE, &cull);
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &t0);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &t1);
        Gl.BindTexture(Gl.TEXTURE_2D, k.WhiteColor);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, k.Color);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, k.OutFbo);
        Gl.Viewport(0, 0, w, h);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.Disable(Gl.CULL_FACE);
        Gl.UseProgram(g.Prog);
        Gl.Uniform4f(g.Bg, bg[0], bg[1], bg[2], bg[3]);
        Gl.BindVertexArray((uint)g.Vao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray((uint)vao);
        Gl.UseProgram((uint)prog);
        Gl.Viewport(vp[0], vp[1], vp[2], vp[3]);
        if (blend != 0) Gl.Enable(Gl.BLEND);
        if (depth != 0) Gl.Enable(Gl.DEPTH_TEST);
        if (cull != 0) Gl.Enable(Gl.CULL_FACE);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)t1);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)t0);
        Gl.ActiveTexture((uint)active);
    }
}

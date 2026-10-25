using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Screen-space reflections, deferred. While the opaque set, the sky, the cutouts and grass draw, the frame
/// target carries the reflection record (<c>cx_ssrrec</c>: normal, roughness, view depth, and the weight the
/// lit stage gave its probe term). At <see cref="PassId.Reflections"/> a half-res pass traces one recorded
/// pixel per 2x2 block through THIS frame's depth and colour, and a full-res pass adds the difference on.
/// </summary>
internal static unsafe class Ssr
{
    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so a name from a dead
    /// context cannot survive by being a field nobody remembered to zero.</summary>
    private sealed class SsrState
    {
        public uint Fbo, Col, HiZ, RecN, RecW, Out, ProgHiZ, ProgTrace, ProgComp;
        public int W, H, LevelCount, LocLevel, LocNf;
        public int TProj, TInvProj, TView, TInvView, TNf, TSsr, TViz, TPlane, TColShift, CCap, CViz;
        /// <summary>The trace's size: half the frame's, rounded up.</summary>
        public int Ow, Oh;
        /// <summary>The frame level the colour copy's level 0 stands at: 1 when the trace's finest level is 1 or 2,
        /// which never reads the full-res levels; the copy is then one bilinear 2:1 blit, an exact box, and its
        /// mips from there.</summary>
        public int ColShift;
        /// <summary>The planar reflection's target: the opaque set drawn mirrored
        /// (<see cref="AlloyRenderer.PlanarPass"/>), a fraction of the frame, mipped for rough surfaces; its
        /// depth. <see cref="PlaneZ"/> is this frame's plane, NaN for none.</summary>
        public uint PlFbo, PlCol, PlDepth;
        public int PlW, PlH;
        public float PlaneZ = float.NaN;
        public bool Armed, Checked;
    }

    private static SsrState _gl = new();
    private static readonly StandDown _down = new("Ssr");
    private static readonly StandDown _plDown = new("Ssr planar");
    private static ExtenderLog? _log;

    /// <summary>Traces this period, for the [alloy] line.</summary>
    private static int _runs;

    /// <summary>Cell visits per ray at each finest level (full, half, quarter): a coarser confirm needs fewer. A ray
    /// grazing the ground it left never skips a coarse cell (the ground's near edge is in it), so it pays ~2 visits a
    /// pixel; 40 ran dry a few hundred pixels short of a building at low camera pitch and left the probe's sky.</summary>
    private static ReadOnlySpan<float> Steps => [160f, 96f, 48f];

    internal static bool On => Lighting.Ssr.V && !_down.Failed;

    /// <summary>The record pair is attached to the frame FBO, so <see cref="GBuffer.ApplyDrawBuffers"/>
    /// routes draw buffers 2..3 at it. The two classes share that list and neither writes it alone.</summary>
    internal static bool RecordsArmed => _gl.Armed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: the names died with it, so the whole GL state goes at once. The planar latch is
    /// forgotten with it -- a target that could not be completed on the dead context may complete on the next.</summary>
    internal static void Forget()
    {
        _gl = new SsrState();
        _plDown.Forget();
    }

    /// <summary>World edge: nothing is carried across frames, so nothing to drop.</summary>
    internal static void Revoke() { }

    internal static string Stats(int frames)
    {
        string s = $"ssr {(On ? "on" : "off")} ({_runs} traces{(_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "")})";
        _runs = 0;
        return s;
    }

    /// <summary>At the opaque pass, with the frame target bound: attach and clear the records. A refusal leaves
    /// them off, which <see cref="Run"/> reads as "the probe alone this frame".</summary>
    internal static void Begin(int* viewport)
    {
        var g = _gl;
        g.Armed = false;
        if (!On || !PostChain.Armed) return;
        if (Gl.BlitFramebuffer == null || Gl.DispatchCompute == null || Gl.BindImageTexture == null || Gl.DrawBuffers == null || Gl.ClearBufferfv == null)
        { _down.Fail(_log, "glBlitFramebuffer, compute, image stores or draw buffers are missing", "screen-space reflections are off for the session"); return; }
        if (g.ProgTrace == 0 && !BuildPrograms()) return;
        if (viewport[0] != 0 || viewport[1] != 0) { _down.Refuse(_log, "the pass viewport is offset", "no reflections this frame"); return; }
        int bound; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &bound);
        if ((uint)bound != PostChain.Fbo) { _down.Refuse(_log, "the colour pass is not on the owned frame target", "no reflections this frame"); return; }
        if (!EnsureTargets(viewport[2], viewport[3], Math.Min(Lighting.SsrDetail.V, 1))) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo); return; }
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, g.RecN, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT3, Gl.TEXTURE_2D, g.RecW, 0);
        // armed BEFORE the list is composed: CX-188's G-buffer shares it, and the composer reads this flag
        g.Armed = true;
        GBuffer.ApplyDrawBuffers();
        float* zero = stackalloc float[4];
        // draw-buffer INDEX 2, not the attachment enum; a zero view depth there is "no record", so the weight needs none
        Gl.ClearBufferfv(Gl.COLOR, 2, zero);
        // once per allocation, not per frame: driver-side validation costs inside the Opaque term
        if (!g.Checked)
        {
            uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            if (status != Gl.FRAMEBUFFER_COMPLETE)
            {
                End();
                _down.Fail(_log, $"the frame target is incomplete with the records attached (0x{status:x})", "screen-space reflections are off for the session");
                return;
            }
            g.Checked = true;
        }
    }

    /// <summary>Around the sky: it writes no record and no G-buffer either, so its fragments stop
    /// paying for either attachment; the cleared record and G0 under it already read "none". Unconditional --
    /// the G-buffer may be armed on a frame Ssr is not. The frame target must be bound.</summary>
    internal static void Pause() => GBuffer.ApplyDrawBuffers(suspend: true);

    internal static void Resume() => GBuffer.ApplyDrawBuffers();

    /// <summary>Detach the records and restore the frame target's single draw buffer. Every frame that reached
    /// <see cref="Begin"/> must come here: the draw-buffer state is the FBO's and outlives the frame.</summary>
    private static void End()
    {
        var g = _gl;
        g.Armed = false;
        GBuffer.ApplyDrawBuffers();
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, 0, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT3, Gl.TEXTURE_2D, 0, 0);
    }

    /// <summary>At <see cref="PassId.Reflections"/>, with the frame target bound: close the records, trace, and add
    /// the result onto the frame. Leaves the frame target bound, depth test on, blend OFF.</summary>
    internal static void Run(int* viewport, Matrix4x4* view, Matrix4x4* proj, float near, float far, int viz)
    {
        var g = _gl;
        if (!g.Armed) return;
        g.Armed = false;
        End();
        // another light viz paints the lit stage's early out, which writes no record
        float planeZ = g.PlaneZ;
        g.PlaneZ = float.NaN;
        if (viz != 0 && viz != 41 && viz != 42 && viz != 45 && viz != 46) return;
        if (!Matrix4x4.Invert(*proj, out Matrix4x4 invProj) || !Matrix4x4.Invert(*view, out Matrix4x4 invView)) return;
        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, PostChain.Fbo);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, g.Fbo);
        Gl.FramebufferTexture2D(Gl.DRAW_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.Col, 0);
        Gl.BlitFramebuffer(0, 0, g.W, g.H, 0, 0, g.W >> g.ColShift, g.H >> g.ColShift, Gl.COLOR_BUFFER_BIT, (uint)(g.ColShift == 0 ? Gl.NEAREST : Gl.LINEAR));
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
            _down.Fail(_log, $"the colour copy raised 0x{err:x}", "screen-space reflections are off for the session");
            return;
        }
        Gl.GenerateTextureMipmap(g.Col);
        BuildPyramid(near, far);

        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.Out, 0);
        Gl.Viewport(0, 0, g.Ow, g.Oh);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        BindUnit(0, Gl.TEXTURE_2D, PostChain.DepthTex);
        BindUnit(1, Gl.TEXTURE_2D, g.Col);
        BindUnit(2, Gl.TEXTURE_2D, g.HiZ);
        BindUnit(3, Gl.TEXTURE_2D, g.RecN);
        BindUnit(4, Gl.TEXTURE_2D, g.RecW);
        bool probe = EnvProbe.Ready && EnvProbe.Tex != 0;
        BindUnit(5, Gl.TEXTURE_CUBE_MAP, probe ? EnvProbe.Tex : 0);
        EnvProbe.BindSh();   // the probe's origin, for the parallax-corrected fetch the lit stage made
        bool planar = !float.IsNaN(planeZ);
        BindUnit(6, Gl.TEXTURE_2D, planar ? g.PlCol : 0);
        Gl.UseProgram(g.ProgTrace);
        Gl.UniformMatrix4fv(g.TProj, 1, 0, (float*)proj);
        Gl.UniformMatrix4fv(g.TInvProj, 1, 0, (float*)&invProj);
        Gl.UniformMatrix4fv(g.TView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(g.TInvView, 1, 0, (float*)&invView);
        Gl.Uniform2f(g.TNf, near, far);
        int fine = Lighting.SsrDetail.V;
        Gl.Uniform4f(g.TSsr, Lighting.SsrRough.V, Steps[fine], probe ? EnvProbe.Mips : 0f, fine);
        Gl.Uniform1i(g.TViz, viz);
        Gl.Uniform1i(g.TColShift, g.ColShift);
        Gl.Uniform4f(g.TPlane, planar ? planeZ : 0f, planar ? 1f : 0f, 0f, 0f);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        for (uint u = 6; u >= 1; u--) BindUnit(u, u == 5 ? Gl.TEXTURE_CUBE_MAP : Gl.TEXTURE_2D, 0);

        bool shown = viz is 41 or 42 or 45 or 46;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        new PassState(depth: false, depthWrite: false, blend: !shown, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
        BindUnit(0, Gl.TEXTURE_2D, g.Out);
        BindUnit(1, Gl.TEXTURE_2D, g.RecN);
        BindUnit(2, Gl.TEXTURE_2D, g.RecW);
        BindUnit(3, Gl.TEXTURE_2D, g.HiZ);
        Gl.UseProgram(g.ProgComp);
        Gl.Uniform1f(g.CCap, Lighting.SsrRough.V);
        Gl.Uniform1i(g.CViz, shown ? viz : 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        for (uint u = 3; u >= 1; u--) BindUnit(u, Gl.TEXTURE_2D, 0);
        BindUnit(0, Gl.TEXTURE_2D, 0);
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        _runs++;
    }

    /// <summary>Open the planar reflection's target for the mirrored draw: 1/<c>light.ssrplanarres</c> of the frame, cleared to alpha 0 (nothing
    /// mirrored), bound with its viewport. False = no planar reflection this frame, and nothing is bound.</summary>
    internal static bool BeginPlanar(int w, int h, out int pw, out int ph)
    {
        var g = _gl;
        int div = Lighting.SsrPlanarRes.V;
        pw = Math.Max(w / div, 1); ph = Math.Max(h / div, 1);
        if (!g.Armed || !Lighting.SsrPlanar.V || Gl.FrontFace == null || _plDown.Failed) return false;
        if (g.PlFbo == 0 || g.PlW != pw || g.PlH != ph)
        {
            if (g.PlFbo == 0) { uint f; Gl.GenFramebuffers(1, &f); g.PlFbo = f; }
            int levels = 1;
            while ((pw >> levels) > 0 || (ph >> levels) > 0) levels++;
            Fresh(ref g.PlCol, Gl.RGBA16F, levels, pw, ph, Gl.LINEAR_MIPMAP_LINEAR, Gl.LINEAR);
            RenderTarget.AllocDepth(ref g.PlDepth, Gl.DEPTH_COMPONENT24, pw, ph);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.PlFbo);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.PlCol, 0);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, g.PlDepth, 0);
            uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
            if (status != Gl.FRAMEBUFFER_COMPLETE)
            {
                g.PlW = g.PlH = 0;
                return _plDown.Fail(_log, $"the planar target is incomplete (0x{status:x})", "mirror reflections are off for the session");
            }
            g.PlW = pw; g.PlH = ph;
            _log?.Info($"Ssr: planar reflection {pw}x{ph} RGBA16F, {levels} mips");
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.PlFbo);
        Gl.Viewport(0, 0, pw, ph);
        float* clear = stackalloc float[4] { 0f, 0f, 0f, 0f };
        float one = 1f;
        Gl.DepthMask(1);
        Gl.ClearBufferfv(Gl.COLOR, 0, clear);
        Gl.ClearBufferfv(Gl.DEPTH, 0, &one);
        return true;
    }

    /// <summary>Close the mirrored draw: mip it for rough surfaces, latch the plane for <see cref="Run"/>, and hand the
    /// frame target back (its record draw buffers are the FBO's own state, so they survive).</summary>
    internal static void EndPlanar(float z)
    {
        var g = _gl;
        Gl.GenerateTextureMipmap(g.PlCol);
        g.PlaneZ = z;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
    }

    private static void BindUnit(uint unit, uint target, uint tex)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(target, tex);
        if (unit == 0) return;
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    private static bool BuildPrograms()
    {
        var g = _gl;
        g.ProgHiZ = GlProgram.Compute("cx_ssrhiz_c", ShaderSource.Get("cx_ssrhiz_c.glsl"), _log, "Ssr hi-Z pyramid");
        g.ProgTrace = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_ssrtrace_f.glsl"), _log, "Ssr trace", content: false);
        g.ProgComp = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_ssrcomp_f.glsl"), _log, "Ssr composite", content: false);
        if (g.ProgHiZ == 0 || g.ProgTrace == 0 || g.ProgComp == 0)
        {
            g.ProgTrace = 0;
            return _down.Fail(_log, "a program did not build", "screen-space reflections are off for the session");
        }
        g.LocLevel = GlProgram.Loc(g.ProgHiZ, "uLevel");
        g.LocNf = GlProgram.Loc(g.ProgHiZ, "uNf");
        g.TProj = GlProgram.Loc(g.ProgTrace, "uProj"); g.TInvProj = GlProgram.Loc(g.ProgTrace, "uInvProj");
        g.TView = GlProgram.Loc(g.ProgTrace, "uView"); g.TInvView = GlProgram.Loc(g.ProgTrace, "uInvView");
        g.TNf = GlProgram.Loc(g.ProgTrace, "uNf"); g.TSsr = GlProgram.Loc(g.ProgTrace, "uSsr"); g.TViz = GlProgram.Loc(g.ProgTrace, "uViz");
        g.TPlane = GlProgram.Loc(g.ProgTrace, "uPlane"); g.TColShift = GlProgram.Loc(g.ProgTrace, "uColShift");
        Gl.UseProgram(g.ProgTrace);
        string[] units = ["uDepth", "uColor", "uHiZ", "uRecN", "uRecW", "uProbe", "uPlanar"];
        for (int i = 0; i < units.Length; i++) Gl.Uniform1i(GlProgram.Loc(g.ProgTrace, units[i]), i);
        Gl.UseProgram(g.ProgComp);
        string[] comp = ["uSrc", "uRecN", "uRecW", "uHiZ"];
        for (int i = 0; i < comp.Length; i++) Gl.Uniform1i(GlProgram.Loc(g.ProgComp, comp[i]), i);
        g.CCap = GlProgram.Loc(g.ProgComp, "uCap"); g.CViz = GlProgram.Loc(g.ProgComp, "uViz");
        Gl.UseProgram(0);
        return true;
    }

    /// <summary>Reduce the frame depth into the pyramid, one dispatch per level. Compute, so no raster state is
    /// touched; the program is put back.</summary>
    private static void BuildPyramid(float near, float far)
    {
        var g = _gl;
        int prev; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        Gl.UseProgram(g.ProgHiZ);
        Gl.Uniform2f(g.LocNf, near, far);
        // cx_ssrhiz_c's uSrc binding: the same unit the trace reads the finished pyramid on
        Gl.ActiveTexture(Gl.TEXTURE0 + 2);
        for (int i = 0; i < g.LevelCount; i++)
        {
            int w = Math.Max(g.W >> i, 1), h = Math.Max(g.H >> i, 1);
            if (i == 0) Gl.BindTexture(Gl.TEXTURE_2D, PostChain.DepthTex);
            else
            {
                // reading level i-1 while level i is the image: legal, the two never alias
                Gl.BindTexture(Gl.TEXTURE_2D, g.HiZ);
                Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
            }
            Gl.Uniform1i(g.LocLevel, i);
            Gl.BindImageTexture(0, g.HiZ, i, 0, 0, Gl.WRITE_ONLY, Gl.R32F);
            Gl.DispatchCompute((uint)(w + 7) / 8, (uint)(h + 7) / 8, 1);
        }
        Gl.MemoryBarrier(Gl.TEXTURE_FETCH_BARRIER_BIT);
        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.R32F);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.UseProgram((uint)prev);
    }

    /// <summary>The records and the pyramid at the frame's size (the records attach beside its colour, so they must
    /// match it), the colour copy (full mip chain, for rough reflections) from level <paramref name="shift"/> of it,
    /// and the trace output at half of it.</summary>
    private static bool EnsureTargets(int w, int h, int shift)
    {
        var g = _gl;
        if (g.Fbo != 0 && g.W == w && g.H == h && g.ColShift == shift) return true;
        g.Checked = false;
        if (g.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); g.Fbo = f; }
        int levels = Levels(w, h);
        int cw = Math.Max(w >> shift, 1), ch = Math.Max(h >> shift, 1);
        g.Ow = (w + 1) / 2; g.Oh = (h + 1) / 2;
        // immutable storage: a new size is a new name
        Fresh(ref g.Col, Gl.RGBA16F, Levels(cw, ch), cw, ch, Gl.LINEAR_MIPMAP_LINEAR, Gl.LINEAR);
        Fresh(ref g.HiZ, Gl.R32F, levels, w, h, Gl.NEAREST_MIPMAP_NEAREST, Gl.NEAREST);
        Fresh(ref g.RecN, Gl.RGBA16F, 1, w, h, Gl.NEAREST, Gl.NEAREST);
        Fresh(ref g.RecW, Gl.R11F_G11F_B10F, 1, w, h, Gl.NEAREST, Gl.NEAREST);
        Fresh(ref g.Out, Gl.RGBA16F, 1, g.Ow, g.Oh, Gl.NEAREST, Gl.NEAREST);
        g.LevelCount = levels;
        g.ColShift = shift;
        int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.Out, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prev);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            g.W = g.H = 0;
            return _down.Fail(_log, $"the trace framebuffer is incomplete (0x{status:x})", "screen-space reflections are off for the session");
        }
        g.W = w; g.H = h;
        _log?.Info($"Ssr: {w}x{h}, records RGBA16F + R11F_G11F_B10F, colour copy RGBA16F {cw}x{ch}, trace {g.Ow}x{g.Oh}, hi-Z {g.LevelCount} levels");
        return true;
    }

    private static int Levels(int w, int h)
    {
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        return levels;
    }

    private static void Fresh(ref uint tex, uint fmt, int levels, int w, int h, int min, int mag)
    {
        uint t;
        GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, tex);
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t); tex = t;
        Gl.TextureStorage2D(tex, levels, fmt, w, h);
        RenderTarget.Clamp(tex, mag);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MIN_FILTER, min);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MAG_FILTER, mag);
    }
}

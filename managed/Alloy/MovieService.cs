using System.Numerics;
using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Ui;

namespace CX.Alloy;

/// <summary>The NDC quad and texture window one movie frame is drawn as: the letterbox fit, as a value.
/// Pure arithmetic over the decoded frame size and the viewport, so it is checkable off-device
/// (<see cref="MovieService.SelfCheck"/>).</summary>
internal readonly unsafe struct MoviePlate
{
    /// <summary>Half-extent of the quad in normalised device coordinates. The larger of the two is always
    /// exactly 1 -- the movie touches the viewport on its long axis and gets bars on the other.</summary>
    internal readonly float Sx, Sy;
    /// <summary>The texture window: Alloy's texture is the decoded frame exactly, so the window is the whole
    /// of it, inset half a texel so a LINEAR fetch never reaches past the edge.</summary>
    internal readonly float U0, U1, V0, V1;

    private MoviePlate(float sx, float sy, float u0, float u1, float v0, float v1)
    { Sx = sx; Sy = sy; U0 = u0; U1 = u1; V0 = v0; V1 = v1; }

    /// <summary>Fit a frame into the SCREEN, preserving its aspect. The port of <c>ShowFrame</c>'s ortho,
    /// whose terms are the engine's own window size and not the GL viewport: <c>SetOrthoTransform(-right,
    /// right, -top, top)</c> around a fixed [-1,1] quad is the same picture as a [-1/right, 1/right]
    /// clip-space quad, and the GUI scale it multiplies into both terms cancels, so it is not a term here.</summary>
    internal static MoviePlate Fit(int frameW, int frameH, int screenW, int screenH)
    {
        float a = (float)frameW / screenW, b = (float)frameH / screenH;
        float m = a > b ? a : b;
        return new MoviePlate(a / m, b / m,
                              0.5f / frameW, (frameW - 0.5f) / frameW,
                              0.5f / frameH, (frameH - 0.5f) / frameH);
    }

    /// <summary>The four triangle-strip vertices (x, y, u, v), screen-top at V0: the decoder uploads the
    /// frame's first row first, and the engine's quad maps it to the top of the screen.</summary>
    internal void Write(Span<Vector4> v)
    {
        v[0] = new Vector4(-Sx, Sy, U0, V0);
        v[1] = new Vector4(-Sx, -Sy, U0, V1);
        v[2] = new Vector4(Sx, Sy, U1, V0);
        v[3] = new Vector4(Sx, -Sy, U1, V1);
    }
}


/// <summary>The movie's pixels are CX's: <see cref="MovieFrame"/> takes them from the decode, and the hook on
/// <c>ShowFrame</c> @0x1165e30 records which frame the coming present owes. Drawn in ALLOY's context into the
/// present image, over the world frame that composited there and before the hand-back blit; the engine keeps
/// its decode loop, which IS the A/V clock. Gated on <c>alloy.enabled</c>, not <see cref="AlloyGate"/>.</summary>
public sealed unsafe partial class MovieService : IExtenderService
{
    private static ExtenderLog? _log;
    private static MovieShowFrameFn _orig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SelfCheck(log);
        // Read off the knob, not asked of AlloyGate, so this does not depend on service discovery order --
        // and so vanilla installs nothing.
        if (!Renderer.Enabled.Boot)
        { log.Info("Movies: standing down (alloy.enabled=0) -- the engine draws its own movie quad"); return; }
        _orig = hooks.InstallMovieShowFrame(&OnShowFrame);
        if (_orig == null)
        { log.Info("Movies: CExoMoviePlayerInternal::ShowFrame did not install -- the engine keeps the movie draw"); return; }
        MovieFrame.Install(log, hooks);
        FrameRegistry.Global.Register(FramePhase.PrePresent, LastBeforePresent, DrawPending, "Movie.frame", log);
        FrameRegistry.Global.Register(FramePhase.PostPresent, 5, AgePresent, "Movie.onscreen", log);
        log.Info("Movies: Alloy draws the movie frame from the decode, in its own context and into the present "
               + "image (the engine keeps the decode, the pacing and the present)");
    }

    // ---- "a movie owns the screen" ----

    /// <summary>Presents since the last movie frame was drawn, saturating. Two, not one: the roll-call runs
    /// before the present it belongs to, so "this present" is not yet a count.</summary>
    private static int _idle = Stale;
    private const int Stale = 2;

    /// <summary>Is a movie covering the screen? While one is, <see cref="UiHostService"/> holds the window
    /// roll-call back -- chat over a cinematic is not a feature. The engine's own <c>CGuiMan</c> site is
    /// already gated on <c>GetIsPlayingMovie()</c>; this flag is what stops <c>UiHostService.OnSwap</c>'s
    /// present-time roll-call, which on Windows is the only one there is, landing on top of the movie.</summary>
    internal static bool OnScreen => _idle < Stale;

    private static void AgePresent() { if (_idle < Stale) _idle++; }

    /// <summary>The <c>ShowFrame</c> call the coming present owes. <c>CExoMoviePlayerInternal::RenderFrame</c>
    /// swaps in the same call that showed the frame, so <see cref="Self"/> is alive when it is drawn.</summary>
    private sealed class PendingState
    {
        internal IntPtr Self;
        internal int FrameW, FrameH, TexW, TexH;
        internal bool Due;
    }
    private static PendingState _pending = new();

    /// <summary>After every other <c>PrePresent</c> participant: a module's movie plays over a loaded area, the
    /// engine still asks for the world view, and Alloy composites that view into the present image at the
    /// frame boundary -- a movie drawn before it is covered.</summary>
    private const int LastBeforePresent = 90;

    [UnmanagedCallersOnly]
    private static void OnShowFrame(IntPtr self, int frameW, int frameH, int texW, int texH)
    {
        _idle = 0;
        PendingState p = _pending;
        p.Self = self; p.FrameW = frameW; p.FrameH = frameH; p.TexW = texW; p.TexH = texH; p.Due = true;
    }

    private static void DrawPending()
    {
        PendingState p = _pending;
        if (!p.Due) return;
        p.Due = false;
        if (Renderer.Movie.V && Draw(p.FrameW, p.FrameH, p.TexW, p.TexH)) return;
        // The engine draws this frame, in its own context and over the screen: its quad opens with Clear(1),
        // so ScreenOwner must let that one through.
        ScreenOwner.EngineFrame = true;
        if (_orig != null) _orig(p.Self, p.FrameW, p.FrameH, p.TexW, p.TexH);
        ScreenOwner.EngineFrame = false;
        ScreenOwner.GlDrewScreen();
    }

    // ---- the draw ----

    /// <summary>Everything the draw owns: the GL names, which belong to ALLOY's 4.6 context, and the latches
    /// that keep a refusal from re-failing twenty-four times a second. Never replaced --
    /// <see cref="ContextLost"/> drops the names alone, so a context that went does not un-say a refusal.</summary>
    private sealed class MovieDrawState
    {
        internal uint Prog, Vao, Vbo;
        internal bool ProgTried, VaoUp;
        internal bool Stood, Announced, SaidNoContext, SaidNoScreen;
    }

    private static readonly MovieDrawState _s = new();

    /// <summary>The 4.6 context went and the program and the quad's buffers went with it: the names are
    /// dropped, not deleted, so the next movie frame rebuilds them instead of drawing through dead ones.</summary>
    internal static void ContextLost()
    {
        MovieDrawState s = _s;
        s.Prog = 0; s.Vao = 0; s.Vbo = 0; s.ProgTried = false; s.VaoUp = false;
    }

    /// <summary>The window size the engine lays its own movie quad out in: <c>ShowFrame</c>'s ortho is built
    /// from these, and <see cref="PresentTarget"/>'s image is sized to them.</summary>
    private static readonly int* ScreenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
    private static readonly int* ScreenH = (int*)Native.Sym(SymbolMap.ScreenHeight);

    /// <summary>Our own mechanism failing is loud and hands the frame back -- never a quiet fallback.
    /// Said once, and from then on the engine's quad draws every frame: a refusal is for the session, so the
    /// movie plays vanilla rather than re-failing per frame behind one stale log line.</summary>
    private static bool Stand(string why)
    {
        MovieDrawState s = _s;
        if (s.Stood) return false;
        s.Stood = true;
        _log?.Always($"Movies: Alloy cannot draw this movie frame -- {why}. The engine's own quad is drawing it "
                   + "(set alloy.movie 0 to make that the deliberate choice).");
        return false;
    }

    /// <summary>Something the frame needs is not there YET -- the context has not come up or a render thread
    /// holds it, the engine has not sized its window. Not this mechanism failing, so it is said once per
    /// reason and NOT latched: this frame is the engine's quad's, and the next one may well draw.</summary>
    private static bool NotYet(ref bool said, string why)
    {
        if (said) return false;
        said = true;
        _log?.Always($"Movies: {why} -- the engine's own movie quad draws the movie until Alloy can");
        return false;
    }

    /// <summary>Put the frame on the screen: one 4.6 bracket around the upload and the quad, then the engine
    /// context's hand-back blit. False = the engine's own quad should draw this frame instead.</summary>
    private static bool Draw(int frameW, int frameH, int texW, int texH)
    {
        if (_log == null || _s.Stood) return false;
        if (frameW <= 0 || frameH <= 0 || texW < frameW || texH < frameH)
            return Stand($"the decoder handed us a {frameW}x{frameH} frame in a {texW}x{texH} texture");
        // The engine's own window size, read before the bracket: ShowFrame's ortho is built from it and the
        // present image is sized to it, where the live GL viewport is whatever the last pass left. A zero
        // READ is transient -- the engine sizes its window late -- so only the symbols are fatal.
        if (ScreenW == null || ScreenH == null) return Stand("g_nScreenWidth/Height did not resolve");
        int w = *ScreenW, h = *ScreenH;
        if (w <= 0 || h <= 0) return NotYet(ref _s.SaidNoScreen, $"the engine's screen size reads {w}x{h}");
        if (Renderer.OnVulkan) return DrawVk(frameW, frameH, w, h);
        if (!Gl.Init(_log)) return Stand("the GL entry points did not resolve");
        if (Gl.ClearBufferfv == null || Gl.ColorMask == null || Gl.BindFramebuffer == null || Gl.Viewport == null)
            return Stand("a core GL entry point did not resolve");
        // The bracket this opens must be the OUTERMOST one: a nested Leave only drains errors and leaves the
        // 4.6 context current, so the hand-back after it would blit in the wrong context.
        if (GlContextService.Inside)
            return NotYet(ref _s.SaidNoContext, "a 4.6 bracket was already open at the movie draw");
        if (!GlContextService.Enter())
            return NotYet(ref _s.SaidNoContext, "Alloy's GL 4.6 context cannot be entered from the game thread");

        bool drew;
        try { drew = InBracket(frameW, frameH, texW, texH, w, h); }
        finally { Neutralise(); GlContextService.Leave(); }
        // Engine context, bracket closed: the blit that puts the present image on the presented buffer. A
        // world frame handed the image back at the frame boundary already, so this one carries the movie.
        if (drew) PresentTarget.HandBack();
        return drew;
    }

    /// <summary>The upload and the quad, with Alloy's context current. Every name it binds is Alloy's, so
    /// nothing here reaches GLRender's shadow cache and there is no engine state to hand back.</summary>
    private static bool InBracket(int frameW, int frameH, int texW, int texH, int w, int h)
    {
        MovieDrawState s = _s;
        // Clear the queue first, or an error some other pass left there would be read as this draw's.
        Gl.DrainErrors();
        // Alloy's own texture, filled from the decode in this same bracket. 0 = this frame is the engine's,
        // and MovieFrame said why -- a refusal there is not this draw's to latch.
        uint tex = MovieFrame.Upload(frameW, frameH);
        if (tex == 0) return false;
        if (!EnsureProgram()) return Stand("the movie program did not build");
        if (!EnsureVao()) return Stand("the movie VAO did not build");
        PresentTarget.Ensure(w, h);
        if (!PresentTarget.Usable) return Stand("there is nowhere in this context for the frame to land");

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PresentTarget.Fbo46);
        Gl.Viewport(0, 0, w, h);
        new PassState(depth: false, depthWrite: false, srgb: false).Apply();
        Gl.Disable(Gl.STENCIL_TEST);
        Gl.ColorMask(1, 1, 1, 1);
        // Black is what a letterbox is, and clearing the attachment says so without touching the context's
        // own clear colour, which the next frame's passes read.
        float* black = stackalloc float[4] { 0f, 0f, 0f, 1f };
        Gl.ClearBufferfv(Gl.COLOR, 0, black);

        Vector4* verts = stackalloc Vector4[4];
        MoviePlate.Fit(frameW, frameH, w, h).Write(new Span<Vector4>(verts, 4));

        Gl.UseProgram(s.Prog);
        Gl.BindVertexArray(s.Vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, s.Vbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(4 * sizeof(Vector4)), verts, Gl.DYNAMIC_DRAW);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        // A sampler object another pass left on unit 0 would override the texture's own filter and clamp.
        if (Gl.BindSampler != null) Gl.BindSampler(0, 0);
        Gl.DrawArrays(Gl.TRIANGLE_STRIP, 0, 4);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);

        // Read before the bracket closes: a draw that errored drew nothing and the present image is then the
        // clear alone, which is the one way this can fail with nothing to show for it.
        uint err = Gl.GetError != null ? Gl.GetError() : 0;
        if (err != 0) return Stand($"the draw raised GL error 0x{err:x4}");

        if (!s.Announced)
        {
            s.Announced = true;
            _log?.Info($"Movies: first frame drawn by Alloy -- {frameW}x{frameH} from the decode (the engine's own "
                     + $"texture is {texW}x{texH} and unread), into the {w}x{h} present image inside Alloy's "
                     + "bracket; the engine's ShowFrame quad is swallowed from here on");
        }
        return true;
    }

    /// <summary>Leave the bracket the way <c>GuiPass.Draw</c> leaves its own: whatever bracket comes next
    /// inherits this state, and a movie frame is the one that can precede a world frame or a GUI-only flush.
    /// Run in the <c>finally</c>, after the draw's own error read, so a restore's error is not read as its.</summary>
    private static void Neutralise()
    {
        Gl.BindVertexArray(0);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
        Gl.UseProgram(0);
        Gl.DepthMask(1);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>The movie quad's program, in Alloy's context: one try, and a failure stands the draw down.</summary>
    private static bool EnsureProgram()
    {
        MovieDrawState s = _s;
        if (s.ProgTried) return s.Prog != 0;
        s.ProgTried = true;
        s.Prog = GlProgram.Graphics(ShaderSource.Get("cx_movie_v.glsl"), ShaderSource.Get("cx_movie_f.glsl"),
                                    _log, "Alloy movie");
        return s.Prog != 0;
    }

    /// <summary>The quad's vertex array and the four-vertex buffer behind it, in Alloy's context: position and
    /// texture coordinate interleaved the way <see cref="MoviePlate.Write"/> writes them.</summary>
    private static bool EnsureVao()
    {
        MovieDrawState s = _s;
        if (s.VaoUp) return s.Vao != 0 && s.Vbo != 0;
        s.VaoUp = true;
        uint a; Gl.GenVertexArrays(1, &a); s.Vao = a;
        uint b; Gl.GenBuffers(1, &b); s.Vbo = b;
        if (s.Vao == 0 || s.Vbo == 0) return false;
        Gl.BindVertexArray(s.Vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, s.Vbo);
        int stride = 4 * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.BindVertexArray(0);
        return true;
    }
}

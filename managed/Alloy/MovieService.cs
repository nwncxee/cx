using System.Numerics;
using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Ui;

namespace CX.Alloy;

/// <summary>The NDC quad and texture window one movie frame is drawn as: the letterbox fit, as a value.
/// Pure arithmetic over the decoded frame size, the texture size and the viewport, so it is checkable
/// off-device (<see cref="MovieService.SelfCheck"/>).</summary>
internal readonly unsafe struct MoviePlate
{
    /// <summary>Half-extent of the quad in normalised device coordinates. The larger of the two is always
    /// exactly 1 -- the movie touches the viewport on its long axis and gets bars on the other.</summary>
    internal readonly float Sx, Sy;
    /// <summary>The texture window: the decoded frame occupies only the top-left <c>frame/tex</c> corner of a
    /// padded texture, and the bounds are inset by half a texel so a LINEAR fetch never reaches the pad.</summary>
    internal readonly float U0, U1, V0, V1;

    private MoviePlate(float sx, float sy, float u0, float u1, float v0, float v1)
    { Sx = sx; Sy = sy; U0 = u0; U1 = u1; V0 = v0; V1 = v1; }

    /// <summary>Fit a frame into the SCREEN, preserving its aspect. The port of <c>ShowFrame</c>'s ortho,
    /// whose terms are the engine's own window size and not the GL viewport: <c>SetOrthoTransform(-right,
    /// right, -top, top)</c> around a fixed [-1,1] quad is the same picture as a [-1/right, 1/right]
    /// clip-space quad, and the GUI scale it multiplies into both terms cancels, so it is not a term here.</summary>
    internal static MoviePlate Fit(int frameW, int frameH, int texW, int texH, int screenW, int screenH)
    {
        float a = (float)frameW / screenW, b = (float)frameH / screenH;
        float m = a > b ? a : b;
        return new MoviePlate(a / m, b / m,
                              0.5f / texW, (frameW - 0.5f) / texW,
                              0.5f / texH, (frameH - 0.5f) / texH);
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

/// <summary>The movie's pixels are CX's. One hook, on <c>ShowFrame</c> @0x1165e30, records the frame; it is drawn
/// last before the present, over the world frame Alloy resolves there. The engine keeps its decode loop, which IS
/// the A/V clock. Engine context, gated on <c>alloy.enabled</c> rather than <see cref="AlloyGate"/>.</summary>
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
        FrameRegistry.Global.Register(FramePhase.PrePresent, LastBeforePresent, DrawPending, "Movie.frame", log);
        FrameRegistry.Global.Register(FramePhase.PostPresent, 5, AgePresent, "Movie.onscreen", log);
        log.Info("Movies: Alloy draws the movie frame (the engine keeps the decode, the pacing and the present)");
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
    /// engine still asks for the world view, and Alloy resolves it at the present -- a frame drawn earlier is covered.</summary>
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
        if (Renderer.Movie.V && Draw(p.Self, p.FrameW, p.FrameH, p.TexW, p.TexH)) return;
        // The engine draws this frame, and its quad opens with Clear(1): ScreenOwner must let that one through.
        ScreenOwner.EngineFrame = true;
        if (_orig != null) _orig(p.Self, p.FrameW, p.FrameH, p.TexW, p.TexH);
        ScreenOwner.EngineFrame = false;
    }

    // ---- the draw ----

    /// <summary>Names in the ENGINE's context, which lives for the process: the 4.6 context-loss edge is not
    /// theirs, and no other edge exists to clear them on.</summary>
    private static uint _prog, _vao, _vbo, _sampler;
    private static bool _samplerSaid;
    private static int _uTex;
    private static bool _progTried, _vaoUp;
    private static bool _stood, _announced;

    /// <summary>The window size the engine lays its own movie quad out in: <c>ShowFrame</c>'s ortho is built
    /// from these and not from the GL viewport, which <c>GLRender::ResetRenderStates</c> never restores.</summary>
    private static readonly int* ScreenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
    private static readonly int* ScreenH = (int*)Native.Sym(SymbolMap.ScreenHeight);

    /// <summary>Our own mechanism failing is loud and hands the frame back -- never a quiet fallback (ADR 0025).
    /// Said once, and from then on the engine's quad draws every frame: a refusal is for the session, so the
    /// movie plays vanilla rather than re-failing per frame behind one stale log line.</summary>
    private static bool Stand(string why)
    {
        if (_stood) return false;
        _stood = true;
        _log?.Always($"Movies: Alloy cannot draw this movie frame -- {why}. The engine's own quad is drawing it "
                   + "(set alloy.movie 0 to make that the deliberate choice).");
        return false;
    }

    /// <summary>Clear the screen and put the frame on it. False = the engine's quad should run instead.</summary>
    private static bool Draw(IntPtr self, int frameW, int frameH, int texW, int texH)
    {
        if (_log == null || _stood) return false;
        if (frameW <= 0 || frameH <= 0 || texW < frameW || texH < frameH)
            return Stand($"the decoder handed us a {frameW}x{frameH} frame in a {texW}x{texH} texture");
        if (!Gl.Init(_log)) return Stand("the GL entry points did not resolve");
        if (Gl.Clear == null || Gl.ClearColor == null || Gl.GetFloatv == null || Gl.ColorMask == null
            || Gl.BindFramebuffer == null || Gl.Viewport == null)
            return Stand("a core GL entry point did not resolve");
        uint tex = new CExoMoviePlayerInternal(self).Texture;
        if (tex == 0) return Stand("the movie player holds no GL texture");
        if (!EnsureProgram()) return Stand("the movie program did not build");

        // The engine's own size, with the live viewport only as the fallback: a movie owns the whole window,
        // and taking the rect from GL state means inheriting whatever rect the last pass of the last frame
        // happened to leave there.
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        int w = ScreenW != null && *ScreenW > 0 ? *ScreenW : vp[2];
        int h = ScreenH != null && *ScreenH > 0 ? *ScreenH : vp[3];
        if (w <= 0 || h <= 0) return Stand($"the screen is {w}x{h}");

        if (!_vaoUp) SetupVao();
        if (_vao == 0 || _vbo == 0) return Stand("the movie VAO did not build");

        // The screen, not an Alloy target: ResetRenderStates binds the engine's default framebuffer at every
        // present, but a movie frame is drawn before one, so nothing else has promised what is bound here.
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, ScreenOwner.DefaultFbo);
        if (w != vp[2] || h != vp[3]) Gl.Viewport(0, 0, w, h);

        // The engine's Clear(1) is scissor-limited like ours, and its clear colour is whatever the last
        // pass left; black is what a letterbox is, so it is set explicitly and put back.
        float* clear = stackalloc float[4];
        Gl.GetFloatv(Gl.COLOR_CLEAR_VALUE, clear);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.Disable(Gl.CULL_FACE);
        Gl.Disable(Gl.BLEND);
        Gl.ColorMask(1, 1, 1, 1);
        Gl.ClearColor(0f, 0f, 0f, 1f);
        Gl.Clear(Gl.COLOR_BUFFER_BIT);
        Gl.ClearColor(clear[0], clear[1], clear[2], clear[3]);

        Vector4* verts = stackalloc Vector4[4];
        MoviePlate.Fit(frameW, frameH, texW, texH, w, h).Write(new Span<Vector4>(verts, 4));

        Gl.UseProgram(_prog);
        Gl.Uniform1i(_uTex, 0);
        Gl.BindVertexArray(_vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(4 * sizeof(Vector4)), verts, Gl.DYNAMIC_DRAW);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        // A sampler object, never glTexParameteri: the texture is the engine's and it caches what it set on
        // it. Its own filter here is NEAREST; LINEAR is a deliberate difference.
        if (_sampler == 0 && (_sampler = Gl.LinearClampSampler()) == 0 && !_samplerSaid)
        {
            _samplerSaid = true;
            _log?.Always("MovieService: no sampler object -- movie frames sample with the engine's NEAREST filter");
        }
        if (_sampler != 0 && Gl.BindSampler != null) Gl.BindSampler(0, _sampler);
        Gl.DrawArrays(Gl.TRIANGLE_STRIP, 0, 4);
        if (_sampler != 0 && Gl.BindSampler != null) Gl.BindSampler(0, 0);

        // Read before the hand-back, which issues engine GL of its own: a draw that errored drew nothing and
        // the screen is then the clear alone, which is the one way this can fail with nothing to show for it.
        uint err = Gl.GetError != null ? Gl.GetError() : 0;

        if (w != vp[2] || h != vp[3]) Gl.Viewport(vp[0], vp[1], vp[2], vp[3]);
        // Raw GL in the engine's context goes past GLRender's shadow cache, and the decode loop binds this
        // very texture again on its next packet -- a stale entry there would upload into what WE left bound.
        UiHostService.HandBack();
        Gl.DrainErrors(4);
        if (err != 0) return Stand($"the draw raised GL error 0x{err:x4}");

        if (!_announced)
        {
            _announced = true;
            _log.Info($"Movies: first frame drawn by Alloy, last before the present -- {frameW}x{frameH} in a {texW}x{texH} texture, "
                    + $"screen {w}x{h} (GL viewport {vp[2]}x{vp[3]}); the engine's ShowFrame quad is swallowed from here on");
        }
        return true;
    }

    private const string VSrc =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\nout vec2 vUV;\n" +
        "void main(){ gl_Position = vec4(aPos, 0.0, 1.0); vUV = aUV; }\n";
    /// <summary>Straight out, no colour-space step: the stage the engine draws this quad through is one of
    /// its UNLIT ones (<c>fsglu</c>), which neither linearises the texel nor re-encodes the result, and
    /// <c>FRAMEBUFFER_SRGB</c> is off client-wide. Alpha is forced to 1 -- blending is off, so the engine's
    /// texel alpha reaches nothing but the unused default-framebuffer alpha channel.</summary>
    private const string FSrc =
        "#version 330 core\nin vec2 vUV;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ o = vec4(texture(uTex, vUV).rgb, 1.0); }\n";

    private static bool EnsureProgram()
    {
        if (_progTried) return _prog != 0;
        _progTried = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrc);
        if (vs == 0 || fs == 0) return false;
        _prog = Gl.CreateProgram();
        Gl.AttachShader(_prog, vs); Gl.AttachShader(_prog, fs); Gl.LinkProgram(_prog);
        int ok; Gl.GetProgramiv(_prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _prog = 0; return false; }
        _uTex = GlProgram.Loc(_prog, "uTex");
        return true;
    }

    private static uint Compile(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        byte[] bytes = Encoding.UTF8.GetBytes(src);
        fixed (byte* sp = bytes)
        {
            byte* pp = sp; int len = bytes.Length;
            Gl.ShaderSource(sh, 1, &pp, &len);
        }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) return sh;
        byte* buf = stackalloc byte[512]; int n = 0;
        Gl.GetShaderInfoLog(sh, 512, &n, buf);
        _log?.Always($"Movies: shader compile failed: {Marshal.PtrToStringAnsi((IntPtr)buf, n)}");
        return 0;
    }

    private static void SetupVao()
    {
        _vaoUp = true;
        uint a; Gl.GenVertexArrays(1, &a); _vao = a;
        uint b; Gl.GenBuffers(1, &b); _vbo = b;
        if (_vao == 0 || _vbo == 0) return;
        Gl.BindVertexArray(_vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        int stride = 4 * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.BindVertexArray(0);
    }
}

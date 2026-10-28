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

    private MoviePlate(float sx, float sy) { Sx = sx; Sy = sy; }

    /// <summary>Fit a frame into the SCREEN, preserving its aspect. The port of <c>ShowFrame</c>'s ortho,
    /// whose terms are the engine's own window size and not the GL viewport: <c>SetOrthoTransform(-right,
    /// right, -top, top)</c> around a fixed [-1,1] quad is the same picture as a [-1/right, 1/right]
    /// clip-space quad, and the GUI scale it multiplies into both terms cancels, so it is not a term here.</summary>
    internal static MoviePlate Fit(int frameW, int frameH, int screenW, int screenH)
    {
        float a = (float)frameW / screenW, b = (float)frameH / screenH;
        float m = a > b ? a : b;
        return new MoviePlate(a / m, b / m);
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
        if (!Renderer.On)
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
        if (Renderer.OnVulkan)
        {
            // the engine's quad is GL, which nothing on Vulkan presents: Vulkan draws the movie or nothing does
            if (!Renderer.Movie.V && !_s.KnobSaid)
            {
                _s.KnobSaid = true;
                _log?.Always("Movies: alloy.movie 0 hands the movie to the engine's GL quad, which a Vulkan session never "
                           + "presents -- Vulkan draws the movie regardless");
            }
            Draw(p.FrameW, p.FrameH, p.TexW, p.TexH);
            return;
        }
        if (Renderer.Movie.V && Draw(p.FrameW, p.FrameH, p.TexW, p.TexH)) return;
        // The engine draws this frame, in its own context and over the screen: its quad opens with Clear(1),
        // so ScreenOwner must let that one through.
        ScreenOwner.EngineFrame = true;
        if (_orig != null) _orig(p.Self, p.FrameW, p.FrameH, p.TexW, p.TexH);
        ScreenOwner.EngineFrame = false;
    }

    // ---- the draw ----

    /// <summary>Everything the draw owns: the GL names, which belong to ALLOY's 4.6 context, and the latches
    /// that keep a refusal from re-failing twenty-four times a second. Never replaced --
    /// <see cref="ContextLost"/> drops the names alone, so a context that went does not un-say a refusal.</summary>
    private sealed class MovieDrawState
    {
        internal bool Stood, Announced, SaidNoContext, SaidNoScreen, KnobSaid;
    }

    private static readonly MovieDrawState _s = new();

    /// <summary>The window size the engine lays its own movie quad out in: <c>ShowFrame</c>'s ortho is built
    /// from these, and <see cref="PresentTarget"/>'s image is sized to them.</summary>
    private static readonly int* ScreenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
    private static readonly int* ScreenH = (int*)Native.Sym(SymbolMap.ScreenHeight);

    /// <summary>Our own mechanism failing is loud and hands the frame back -- never a quiet fallback.
    /// Said once, and from then on the engine's quad draws every frame -- on Vulkan, nothing does: a refusal is for
    /// the session, rather than re-failing per frame behind one stale log line.</summary>
    private static bool Stand(string why)
    {
        MovieDrawState s = _s;
        if (s.Stood) return false;
        s.Stood = true;
        _log?.Always($"Movies: Vulkan cannot draw this movie -- {why}. NO movie is drawn for the rest of the "
                   + "session: the screen holds its last frame while one plays");
        return false;
    }

    /// <summary>Something the frame needs is not there YET -- the context has not come up or a render thread
    /// holds it, the engine has not sized its window. Not this mechanism failing, so it is said once per
    /// reason and NOT latched: this frame is the engine's quad's (nobody's on Vulkan), and the next one may well draw.</summary>
    private static bool NotYet(ref bool said, string why)
    {
        if (said) return false;
        said = true;
        _log?.Always($"Movies: {why} -- the movie is NOT DRAWN until Vulkan can take it");
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
        return DrawVk(frameW, frameH, w, h);
    }
}

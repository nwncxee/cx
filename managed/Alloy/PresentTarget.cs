using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Where Alloy's frame lands when the 4.6 context and the engine's do not agree about the default
/// framebuffer: under WGL the two address back buffers that fall out of phase across each swap, so Alloy
/// composites into a shared TEXTURE and the engine context blits that to the real default framebuffer after
/// the bracket -- each side over its own framebuffer OBJECT, which is not shared. Windows only; GLX is fine.</summary>
internal static unsafe class PresentTarget
{
    /// <summary>Does this platform need the frame routed through a texture? Folded to a constant.</summary>
    internal static bool Needed => OperatingSystem.IsWindows();

    private static uint _tex, _fbo46, _fboEng;
    private static int _w, _h;
    private static ExtenderLog? _log;
    private static bool _announced, _blitFailed;
    private static ComposeState _compose = new();

    /// <summary>Whether the world frame has composited into the texture since the last hand-back.</summary>
    private sealed class ComposeState { internal bool Composed; }

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The framebuffer the 4.6 context should bind where it would otherwise bind 0. Zero means
    /// "bind the default framebuffer" -- Linux, or a frame whose target could not be built.</summary>
    internal static uint Fbo46 => _fbo46;

    /// <summary>Size the present colour texture to the default framebuffer, once per frame, before anything
    /// binds it.</summary>
    internal static void Ensure(int w, int h)
    {
        if (!Needed || w <= 0 || h <= 0) return;
        if (_tex != 0 && w == _w && h == _h) return;
        // RGBA8: this holds the FINISHED, display-space frame -- the composite has already encoded and
        // dithered it, so anything wider would be storing precision the default framebuffer cannot take.
        RenderTarget.AllocColor(ref _tex, false, w, h, Gl.NEAREST);
        _w = w; _h = h;
        if (_fbo46 == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo46 = f; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo46);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _tex, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        {
            _log?.Always("PresentTarget: the present texture target is incomplete -- falling back to writing "
                       + "framebuffer 0 from the 4.6 context, which flickers on Windows (#456)");
            uint t2 = _tex; Gl.DeleteTextures(1, &t2); _tex = 0; _w = _h = 0;
        }
        else if (!_announced)
        {
            _announced = true;
            _log?.Info($"PresentTarget: Alloy composites into a {w}x{h} texture and the ENGINE context blits it "
                     + "to the default framebuffer -- the two contexts do not share a back buffer here (#456)");
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>The world frame is about to composite into the texture: the GUI flush draws over it as it is
    /// instead of taking the stale screen back over it.</summary>
    internal static void MarkComposed() { if (Needed) _compose.Composed = true; }

    /// <summary>Blit the finished frame into the real default framebuffer. MUST run in the ENGINE's context,
    /// after the bracket has closed: that is the whole point, and calling it inside the bracket reintroduces
    /// the bug it exists to fix.</summary>
    internal static void HandBack()
    {
        if (!Needed || _tex == 0 || _fbo46 == 0) return;
        if (_fboEng == 0) { uint f; Gl.GenFramebuffers(1, &f); _fboEng = f; }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fboEng);
        // Re-attached every frame for the reason BindTarget re-attaches: GL reuses freed names, and a cached
        // attachment can outlive the image it names across a device reset.
        Gl.FramebufferTexture2D(Gl.READ_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _tex, 0);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, 0);
        Gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, Gl.COLOR_BUFFER_BIT, (uint)Gl.NEAREST);
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0 && !_blitFailed)
        {
            _blitFailed = true;
            _log?.Always($"PresentTarget: the hand-back blit returned GL error 0x{err:x} -- the Alloy frame "
                       + "is finished but may not have reached the presented buffer (#456)");
        }
        Gl.DrainErrors(4);
        ScreenOwner.RestoreEngineFbo();
        _compose.Composed = false;
    }

    /// <summary>The present texture and its size, for the GUI pass's own framebuffer (colour = this texture,
    /// depth its own): the GUI draws into the same image the world frame composited into.</summary>
    internal static uint Tex => _tex;
    /// <inheritdoc cref="Tex"/>
    internal static int Width => _w;
    /// <inheritdoc cref="Tex"/>
    internal static int Height => _h;

    /// <summary>Blit a world frame that has composited into the texture but not yet been handed back, so raw
    /// engine-context drawing before the next GUI flush lands over it on the real framebuffer instead of being
    /// blitted over. Engine context, outside the bracket. A no-op on a frame with nothing owed.</summary>
    internal static void HandBackIfOwed() { if (Needed && _compose.Composed) HandBack(); }

    /// <summary>The reverse of <see cref="HandBack"/>, in the ENGINE context: copy the real default framebuffer
    /// INTO the present texture, so a GUI flush on a frame with no world draws over what is on screen now. A frame
    /// the world composited skips it: the screen is last frame's, and copying it would erase the world.</summary>
    internal static void TakeBack()
    {
        if (!Needed || _tex == 0 || _compose.Composed) return;
        if (_fboEng == 0) { uint f; Gl.GenFramebuffers(1, &f); _fboEng = f; }
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _fboEng);
        Gl.FramebufferTexture2D(Gl.DRAW_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _tex, 0);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, 0);
        Gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, Gl.COLOR_BUFFER_BIT, (uint)Gl.NEAREST);
        Gl.DrainErrors(4);
        ScreenOwner.RestoreEngineFbo();
    }

    /// <summary>GL names die with the context. Names only -- the images are gone already.</summary>
    internal static void ContextLost() { _tex = 0; _fbo46 = 0; _fboEng = 0; _w = _h = 0; _compose = new(); }
}

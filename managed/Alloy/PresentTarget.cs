using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>Where GL Alloy's frame lands: a TEXTURE the ENGINE's context blits to the real default framebuffer after
/// the bracket, each side over its own framebuffer object (WGL's two contexts address back buffers that fall out of
/// phase across a swap). Under <c>alloy.backend vk</c> it sizes Vulkan's composite beside it, and Vulkan presents that
/// itself: nothing here reaches the screen.</summary>
internal static unsafe class PresentTarget
{
    /// <summary>The present path's own state: the image, one framebuffer object per context, the size, and
    /// whether the world frame has composited into the image since the last hand-back.</summary>
    private sealed class PresentState
    {
        public uint Tex, Fbo46, FboEng;
        public int W, H;
        public bool Composed, Announced, BlitFailed, RefusedSaid;
        public long HandBacks, TakeBacks;
    }

    private static PresentState _s = new();
    private static ExtenderLog? _log;

    /// <summary>Is framebuffer 0 in the 4.6 context the buffer the engine presents? Under GLX both contexts
    /// name one drawable, so it is; under WGL each holds its own back buffer, so it is not -- which is what
    /// makes a composite image that cannot be built a fallback there and a REFUSAL here.</summary>
    private static bool Fb0IsTheScreen => !OperatingSystem.IsWindows();

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The framebuffer the 4.6 context binds where it would otherwise bind 0. Zero means "bind the
    /// default framebuffer" -- a frame whose present target could not be built.</summary>
    internal static uint Fbo46 => _s.Fbo46;

    /// <summary>Is there anywhere in the 4.6 context for a frame to land -- the composite image, or
    /// framebuffer 0 where it IS the buffer the engine presents? False is the refused Windows case, where
    /// such a draw would go to a back buffer nobody shows.</summary>
    internal static bool Usable => _s.Fbo46 != 0 || Fb0IsTheScreen;

    /// <summary>The present colour texture and its size, for the GUI pass's own framebuffer (colour = this
    /// texture, depth its own): the GUI draws into the same image the world frame composited into.</summary>
    internal static uint Tex => _s.Tex;
    /// <inheritdoc cref="Tex"/>
    internal static int Width => _s.W;
    /// <inheritdoc cref="Tex"/>
    internal static int Height => _s.H;

    /// <summary>Size the present colour image to the default framebuffer, once per frame, before anything
    /// binds it -- in the 4.6 context; under <c>alloy.backend vk</c> Vulkan's composite instead, with no GL.</summary>
    internal static void Ensure(int w, int h)
    {
        PresentState s = _s;
        if (w <= 0 || h <= 0) return;
        // Keyed on the size even when the last attempt failed, so a refusal is said once and retried only on
        // a real resize rather than rebuilding a texture per flush for the rest of the session.
        if (w == s.W && h == s.H) return;
        s.W = w; s.H = h;
        if (Renderer.OnVulkan)
        {
            if (_log == null || AlloyDevice.Vulkan(_log) is not VkBackend vk || !vk.Resize(w, h))
                AlloyGate.Fail($"Vulkan's {w}x{h} composite image was refused (logged above)");
            else if (!s.Announced) { s.Announced = true; _log.Info($"PresentTarget: Vulkan composites into its own {w}x{h} image and presents it"); }
            return;
        }
        // RGBA8: this holds the FINISHED, display-space frame -- the composite has already encoded and dithered it,
        // so anything wider would store precision the default framebuffer cannot take.
        RenderTarget.AllocColor(ref s.Tex, false, w, h, Gl.NEAREST);
        if (s.Tex == 0) { Refuse("the composite image could not be allocated"); return; }
        if (s.Fbo46 == 0) { uint f; Gl.GenFramebuffers(1, &f); s.Fbo46 = f; }
        uint status = Attach();
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, s.Tex); s.Tex = 0;
            Refuse($"the present framebuffer is incomplete (0x{status:x})");
        }
        else if (!s.Announced)
        {
            s.Announced = true;
            _log?.Info($"PresentTarget: Alloy composites into a {w}x{h} texture and the ENGINE context blits it "
                     + "to the default framebuffer -- one present path on every OS");
        }
    }

    /// <summary><see cref="Tex"/> onto the 4.6 framebuffer object; its status, with framebuffer 0 bound after.</summary>
    private static uint Attach()
    {
        PresentState s = _s;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, s.Fbo46);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, s.Tex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return status;
    }

    /// <summary>There is no composite image. The framebuffer object goes with it, so <see cref="Fbo46"/> stops
    /// claiming one and every binder writes framebuffer 0 -- which is the screen on a shared drawable and is
    /// nothing on Windows, where this is a stated refusal and the engine renders the world instead.</summary>
    private static void Refuse(string why)
    {
        PresentState s = _s;
        // Zeroed whether or not the delete can run: the name is what the binders read, and a missing entry
        // point must not turn the path whose job is to fail gracefully into a null call.
        if (s.Fbo46 != 0)
        {
            GpuGraveyard.Retire(GpuGraveyard.Kind.Framebuffer, s.Fbo46);
            s.Fbo46 = 0;
        }
        if (s.RefusedSaid) return;
        s.RefusedSaid = true;
        if (Fb0IsTheScreen)
            _log?.Always($"PresentTarget: {why} -- the 4.6 context writes framebuffer 0 instead, which on this "
                       + "drawable IS the buffer the engine presents, so the frame still reaches the screen");
        else
        {
            // Windows: there is no second place to put the frame, and the GUI flush has none either -- so this
            // says it rather than drawing a frame nobody will see.
            _log?.Always($"PresentTarget: {why} -- and framebuffer 0 in the 4.6 context is NOT the buffer this "
                       + "platform presents, so the Alloy frame has nowhere to land: the world renders vanilla "
                       + "and Alloy's GUI draws do not reach the screen either");
            AlloyGate.Fail(why + " -- the present composite image could not be built");
        }
    }

    /// <summary>The world frame is about to composite into the image: the GUI flush draws over it as it is
    /// instead of taking the stale screen back over it.</summary>
    internal static void MarkComposed() => _s.Composed = true;

    /// <summary>Has the world composited this frame? Its bracket then ran the frame's deferred GL work already.</summary>
    internal static bool Composed => _s.Composed;

    /// <summary>Blit the finished frame into the real default framebuffer. MUST run in the ENGINE's context,
    /// after the bracket has closed: that is the whole point, and calling it inside the bracket reintroduces
    /// the bug it exists to fix. This blit is what the composite costs; <c>general.frametimes</c> times it.</summary>
    internal static void HandBack()
    {
        PresentState s = _s;
        if (Renderer.OnVulkan) { s.Composed = false; return; }
        if (s.Tex == 0 || s.Fbo46 == 0) return;
        if (s.FboEng == 0) { uint f; Gl.GenFramebuffers(1, &f); s.FboEng = f; }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, s.FboEng);
        // Re-attached every frame for the reason BindTarget re-attaches: GL reuses freed names, and a cached
        // attachment can outlive the image it names across a device reset.
        Gl.FramebufferTexture2D(Gl.READ_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, s.Tex, 0);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, 0);
        bool timed = CX.Services.Perf.FrameProbe.PresentBegin();
        Gl.BlitFramebuffer(0, 0, s.W, s.H, 0, 0, s.W, s.H, Gl.COLOR_BUFFER_BIT, (uint)Gl.NEAREST);
        if (timed) CX.Services.Perf.FrameProbe.PresentEnd();
        s.HandBacks++;
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0 && !s.BlitFailed)
        {
            s.BlitFailed = true;
            _log?.Always($"PresentTarget: the hand-back blit returned GL error 0x{err:x} -- the Alloy frame "
                       + "is finished but may not have reached the presented buffer");
        }
        Gl.DrainErrors(4);
        ScreenOwner.RestoreEngineFbo();
        s.Composed = false;
    }

    /// <summary>Blit a world frame that has composited into the image but not yet been handed back, so raw
    /// engine-context drawing before the next GUI flush lands over it on the real framebuffer instead of being
    /// blitted over. Engine context, outside the bracket. A no-op on a frame with nothing owed.</summary>
    internal static void HandBackIfOwed() { if (_s.Composed) HandBack(); }

    /// <summary>The reverse of <see cref="HandBack"/>, in the ENGINE context: copy the real default framebuffer
    /// INTO the present image, so a GUI flush on a frame with no world draws over what is on screen now. A frame
    /// the world composited skips it: the screen is last frame's, and copying it would erase the world.</summary>
    internal static void TakeBack()
    {
        PresentState s = _s;
        if (s.Tex == 0 || s.Composed || Renderer.OnVulkan) return;
        if (s.FboEng == 0) { uint f; Gl.GenFramebuffers(1, &f); s.FboEng = f; }
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, s.FboEng);
        Gl.FramebufferTexture2D(Gl.DRAW_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, s.Tex, 0);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, 0);
        Gl.BlitFramebuffer(0, 0, s.W, s.H, 0, 0, s.W, s.H, Gl.COLOR_BUFFER_BIT, (uint)Gl.NEAREST);
        s.TakeBacks++;
        Gl.DrainErrors(4);
        ScreenOwner.RestoreEngineFbo();
    }

    /// <summary>What the present path cost this session, in blits. One hand-back per GUI flush is the floor; a
    /// take-back beside it is a flush that had to collect engine-context drawing off the screen first.</summary>
    internal static string Stats()
        => _s.W == 0 ? "present: no composite image yet"
         : _s.Tex == 0 ? $"present: framebuffer 0 -- the {_s.W}x{_s.H} composite image was REFUSED"
         : $"present: {_s.W}x{_s.H} composite, {_s.HandBacks} hand-back + {_s.TakeBacks} take-back blits";

    /// <summary>GL names die with the context. Names only -- the images are gone already, so nothing is
    /// released here and the next frame's <see cref="Ensure"/> builds a fresh one.</summary>
    internal static void ContextLost() => _s = new PresentState();
}

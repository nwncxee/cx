using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The reactive mask: the scene colour copied before the beam, particle, halo and weather passes, and the tonemapped
/// change they made written into an R8 target at the render size. FSR 3.1 takes it as its reactive input and the TAA
/// resolve floors the current frame's share with it, so a streak's history does not drag along the ground's velocity.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The copy, the mask, the pass's program and its pipeline on one device.</summary>
    private sealed class VkReactState
    {
        public VkBackend? Dev;
        public ImageId Copy, Mask;
        public int W, H;
        public GuiVkProg? Prog;
        public PipelineId Pipe;
        public CommandList? Side;
        public bool Tried, Live, Failed;
        /// <summary>This frame's copy was taken, so the mask is written and its readers may have it.</summary>
        public bool Armed;
        public int Mode = -1, Knee = -1, AcesMix = -1, Exposure = -1;
    }

    private static VkReactState _vkReact = new();

    /// <summary>Room for the pass's one bind and draw.</summary>
    private const int ReactArenaBytes = 1 << 12;

    /// <summary>The fragment stage that measures the change: <c>CxReactiveMask</c> over the frame's own tone curve.</summary>
    internal static string ReactiveFs => ShaderSource.Get("cx_reactive_f.glsl");

    /// <summary>This frame's reactive mask, or null when the pass sat out -- which FSR reads as its own history's say
    /// and the TAA resolve as a zero floor, the way both drew before the mask existed.</summary>
    internal static ImageId VkReactiveMask => _vkReact.Armed ? _vkReact.Mask : default;

    /// <summary>The scene colour as it stands before the four passes the mask covers. Nothing when neither FSR nor
    /// TAA draws: the mask has no reader then, and a frame copy is not free.</summary>
    private static void VkReactiveCopy(VkBackend vk, CommandList cl, VkColourState c, ExtenderLog log)
    {
        VkReactState s = _vkReact;
        if (s.Dev != vk) _vkReact = s = new VkReactState { Dev = vk };
        s.Armed = false;
        if (_capturing || !(Fsr.On || PostChain.TaaOn) || !VkReactiveEnsure(vk, s, c.W, c.H, log)) return;
        var whole = new ImageRegion(0, 0, 0, 0);
        cl.CopyImage(new CopyImageCmd(c.Hdr, whole, s.Copy, whole, c.W, c.H, 1));
        s.Armed = true;
    }

    /// <summary>The mask those four passes earned, in a pass of its own. Nothing drew: the target is cleared to zero
    /// and no full-screen read of two colour targets is paid for a frame whose change is nowhere.</summary>
    private static void VkReactiveWrite(CommandList cl, VkColourState c, ExtenderLog log)
    {
        VkReactState s = _vkReact;
        if (!s.Armed) return;
        CommandList side = s.Side!;
        side.Reset();
        side.SetViewport(0, 0, s.W, s.H);
        int arena0 = c.ArenaUsed;
        if (c.Effects)
        {
            ReadOnlySpan<ImageId> read = [c.Hdr, s.Copy];
            cl.ReadImages(read);
            // a full arena says so itself and records nothing, which leaves the clear below: a zero mask, not a stale one
            VkReactiveDraw(side, s, c);
        }
        ReadOnlySpan<ColorAttachment> colors = [new(s.Mask, 0, 0, LoadOp.Clear, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors));
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan reactive mask is LIVE at {s.W}x{s.H} -- the tonemapped change the "
                     + "beam, particle, halo and weather passes made, fed to FSR 3.1 and to the TAA resolve");
        }
    }

    /// <summary>The one full-screen triangle: the frame and its copy on the pass's own loose block. Records nothing
    /// when the frame's uniform arena is full, which the arena says itself.</summary>
    private static void VkReactiveDraw(CommandList side, VkReactState s, VkColourState c)
    {
        GuiVkProg p = s.Prog!;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        LooseBlock.Set(s.Mode, Post.Tonemap.V);
        LooseBlock.Set(s.Knee, Post.TmKnee.V);
        LooseBlock.Set(s.AcesMix, Post.AcesMix.V);
        LooseBlock.Set(s.Exposure, Post.Exposure.V);
        LooseBlock.EndCapture();
        int at = p.Block.Length == 0 ? 0 : VkArena(p.Block);
        if (at < 0) return;
        Span<BufferBinding> ubo = stackalloc BufferBinding[2];
        Span<ImageBinding> img = stackalloc ImageBinding[4];
        int nu = 0, ni = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            if (o.Kind == VkBindKind.UniformBlock && nu < ubo.Length)
                ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, at, p.Block.Length)
                                                        : new BufferBinding(o.Binding, c.ZeroUbo);
            else if (o.Kind == VkBindKind.Sampled && ni < img.Length)
                img[ni++] = new ImageBinding(o.Binding, o.Name == "uCopy" ? s.Copy : c.Hdr, c.Linear);
        }
        side.SetPipeline(s.Pipe);
        side.BindSet(0, new BindSet(ubo[..nu], default, img[..ni], default));
        side.Draw(3);
    }

    /// <summary>The program, its pipeline and the two targets at the render size, made once per device and resized
    /// with the view. A refusal is said once and stands the mask down for the device.</summary>
    private static bool VkReactiveEnsure(VkBackend vk, VkReactState s, int w, int h, ExtenderLog log)
    {
        if (s.Failed || w <= 0 || h <= 0) return false;
        if (!s.Tried)
        {
            s.Tried = true;
            s.Side = new CommandList("vulkan reactive", ReactArenaBytes, log);
            // through Content as the resolve's text is: a hak that ships cx_tonemap.shd regrades both, or neither
            s.Prog = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Content(ReactiveFs, log), "reactive mask", q =>
            {
                s.Mode = q.Loc("uMode"); s.Knee = q.Loc("uKnee");
                s.AcesMix = q.Loc("uAcesMix"); s.Exposure = q.Loc("uExposure");
            }, log);
            if (s.Prog != null)
            {
                var d = new PipelineDesc { Vs = s.Prog.Vs, Fs = s.Prog.Fs, Label = "vk reactive mask" };
                d.Targets.ColorCount = 1;
                d.Targets.Colors[0] = Format.R8;
                d.Targets.Samples = 1;
                s.Pipe = vk.CreatePipeline(in d);
            }
            if (s.Prog == null || s.Pipe.IsNull)
            {
                s.Prog = null;
                s.Failed = true;
                log.Always("AlloyRenderer: the Vulkan reactive mask was REFUSED (logged above) -- FSR 3.1 and the TAA "
                         + "resolve keep their own history rules, and a streak may drag over the ground behind it");
            }
        }
        if (s.Prog == null) return false;
        if (!s.Mask.IsNull && s.W == w && s.H == h) return true;
        vk.Destroy(s.Copy);
        vk.Destroy(s.Mask);
        // the copy's lane is free from the water snapshot's last read to the post pair's first write, which is
        // exactly the window it lives in; the mask itself aliases nothing, because FSR and TAA read it in that pair's
        s.Copy = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1,
                                              ImageUse.Sampled | ImageUse.CopyDst, "vk reactive copy", AliasLanes.Wide));
        s.Mask = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8, w, h, 1, 1, 1, 1,
                                              ImageUse.Sampled | ImageUse.ColorTarget, "vk reactive mask"));
        s.W = w; s.H = h;
        if (!s.Copy.IsNull && !s.Mask.IsNull) return true;
        log.Always($"AlloyRenderer: the Vulkan reactive mask at {w}x{h} was REFUSED (logged above) -- the frame draws "
                 + "without it, as it did before the mask existed");
        vk.Destroy(s.Copy);
        vk.Destroy(s.Mask);
        s.Copy = s.Mask = default;
        s.Failed = true;
        return false;
    }
}

using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The movie frame on Vulkan: the decode's rows uploaded into a Vulkan image and drawn letterboxed over a
/// black composite, which Vulkan's present then shows. No GL and no hand-back blit.</summary>
public sealed unsafe partial class MovieService
{
    /// <summary>The plate's program, its image at the frame's size and its command list, on one device.</summary>
    private sealed class MovieVkState
    {
        public VkBackend? Dev;
        public GuiVkProg? Prog;
        public ImageId Img;
        public int W, H;
        public CommandList? Cmds;
        public bool Tried, Announced;
    }

    private static MovieVkState _vk = new();

    /// <summary><see cref="Draw"/> under <c>alloy.backend vk</c>. False = the engine's own quad draws this frame.</summary>
    private static bool DrawVk(int frameW, int frameH, int w, int h)
    {
        if (AlloyDevice.Vulkan(_log!) is not VkBackend vk || !vk.OnOwnThread)
            return NotYet(ref _s.SaidNoContext, "the Vulkan device is not up on the game thread");
        if (vk.Composite.IsNull) return NotYet(ref _s.SaidNoScreen, "Vulkan's composite is not sized yet");
        MovieVkState s = _vk;
        if (s.Dev != vk) _vk = s = new MovieVkState { Dev = vk };
        if (!s.Tried)
        {
            s.Tried = true;
            s.Cmds = new CommandList("vulkan movie", 1 << 12, _log!);
            s.Prog = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_movie_vk_f.glsl"), "movie", _ => { }, _log!);
        }
        if (s.Prog == null || !PostChainVk.Ensure(vk, _log!)) return Stand("the Vulkan movie program was refused (logged above)");
        ReadOnlySpan<byte> rgba = MovieFrame.RgbaVk(frameW, frameH);
        if (rgba.IsEmpty) return false;
        if (s.Img.IsNull || s.W != frameW || s.H != frameH)
        {
            vk.Destroy(s.Img);
            s.Img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, frameW, frameH, 1, 1, 1, 1,
                                                 ImageUse.Sampled | ImageUse.CopyDst, "vk movie frame"));
            s.W = frameW; s.H = frameH;
            if (s.Img.IsNull) return Stand($"the {frameW}x{frameH} Vulkan movie image was refused");
        }
        if (!vk.Upload(s.Img, 0, 0, rgba)) return Stand("the movie frame's upload was refused (logged above)");
        MoviePlate p = MoviePlate.Fit(frameW, frameH, w, h);
        int pw = (int)MathF.Round(p.Sx * w), ph = (int)MathF.Round(p.Sy * h);
        CommandList cl = s.Cmds!;
        cl.Reset();
        PostChainVk.Draw(cl, s.Prog, Format.Rgba8, PostChainVk.Blend.None, vk.Composite, LoadOp.Clear,
                         new Rect((w - pw) / 2, (h - ph) / 2, pw, ph), 0, new PostChainVk.Inputs(s.Img), _log);
        if (!vk.Submit(cl)) return Stand("the movie submit was refused (logged above)");
        if (!s.Announced)
        {
            s.Announced = true;
            _log?.Info($"Movies: first frame drawn by Vulkan -- {frameW}x{frameH} from the decode, letterboxed at {pw}x{ph} "
                     + $"into the {w}x{h} composite Vulkan presents; the engine's ShowFrame quad is swallowed from here on");
        }
        return true;
    }
}

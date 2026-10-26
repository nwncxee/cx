using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class GuiPassVk
{
    /// <summary>Engine GL names the GUI sampled that hold no image to copy, the names the engine deleted since the last
    /// flush (its delete runs on whatever thread it is on), and the copies made.</summary>
    private sealed class EngineTexState
    {
        public readonly HashSet<int> Missing = new();
        public readonly List<uint> Dead = new();
        public long Copied;
    }

    private static EngineTexState _engineTex = new();

    /// <summary>The largest engine texture copied, per side: NUI's images and nuklear's font atlas sit far below it.</summary>
    private const int EngineTexMax = 8192;

    /// <summary>An engine-made GL texture a GUI draw names -- nuklear's font atlas, a NUI image -- copied once into a
    /// Vulkan image under its GL name, which <see cref="VkTextures"/> then resolves. False (said once per name) when
    /// the name holds no 2D image. ponytail: a synchronous readback in the 4.6 context, once per new name; the
    /// upgrade is taking the engine's uploads at their source once the GL context goes.</summary>
    private static bool MirrorEngine(VkBackend vk, int tex, ExtenderLog? log)
    {
        EngineTexState e = _engineTex;
        if (tex <= 0 || (uint)tex >= VkTextures.FirstName || e.Missing.Contains(tex) || !GlContextService.Enter()) return false;
        uint name = (uint)tex;
        byte[]? px;
        int w, h, min, mag, ws, wt;
        try { px = GlTexture.ReadRgba8(name, EngineTexMax, out w, out h, out min, out mag, out ws, out wt); }
        finally { GlContextService.Leave(); }
        if (px == null) return Missing(e, tex, $"holds no 2D image Vulkan can take ({w}x{h})", log);
        SamplerDesc sd = VkTextures.Sampler(min, mag, ws, wt, aniso: false) with { Mip = MipMode.None };
        ImageId img = VkTextures.Create(vk, name, Format.Rgba8, w, h, 1, false, sd, $"engine tex {name}");
        if (img.IsNull || !vk.Upload(img, 0, 0, px)) return Missing(e, tex, "was refused its Vulkan image (logged above)", log);
        if (e.Copied++ == 0)
            log?.Always($"GuiPassVk: engine GL textures the GUI samples (nuklear's font atlas, NUI's images) are copied "
                      + $"into Vulkan images, once per name -- the first, {name}, at {w}x{h}");
        return true;
    }

    private static bool Missing(EngineTexState e, int tex, string why, ExtenderLog? log)
    {
        e.Missing.Add(tex);
        log?.Always($"GuiPassVk: engine GL texture {tex} {why} -- its GUI draws take the transparent stand-in on Vulkan");
        return false;
    }

    /// <summary>The engine deleted <paramref name="name"/>: its copy goes at the next flush, and a later texture under
    /// the name is copied anew. Any thread; cold.</summary>
    internal static void ForgetEngine(uint name)
    {
        if (name == 0 || name >= VkTextures.FirstName) return;
        lock (_engineTex.Dead) _engineTex.Dead.Add(name);
    }

    /// <summary>The deleted names' copies go, on the device's thread, before the flush resolves any texture.</summary>
    private static void DrainEngine()
    {
        EngineTexState e = _engineTex;
        lock (e.Dead)
        {
            foreach (uint name in e.Dead) { e.Missing.Remove((int)name); VkTextures.Forget(name); }
            e.Dead.Clear();
        }
    }

    /// <summary>The engine-texture copies, for <see cref="Stats"/>.</summary>
    private static string EngineStats()
    {
        EngineTexState e = _engineTex;
        return e.Copied == 0 && e.Missing.Count == 0 ? ""
            : $", {e.Copied} engine texture(s) copied" + (e.Missing.Count == 0 ? "" : $", {e.Missing.Count} with no image");
    }
}

using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Graphics;

public sealed unsafe partial class NullGl
{
    /// <summary>The longest a snapshot waits for the composite's copy to land.</summary>
    private const int SnapshotWaitMs = 2000;
    /// <summary>Aurora's texture-format enum the engine's snapshots read in: entry 1 of <c>textureFormatTranslationTable</c>,
    /// GL_RGB with unsigned bytes.</summary>
    private const int AuroraRgb = 1;

    /// <summary>Screenshots and save thumbnails with no GL: the last composite Vulkan presented, read back in the
    /// engine's GL_RGB layout -- rows bottom-up, as the composite already holds them, each padded to 4 bytes. A format other than RGB, or no device on this
    /// thread, leaves the buffer as the engine allocated it and says so once.</summary>
    [UnmanagedCallersOnly]
    private static void OnReadPixels(uint fb, int x, int y, int w, int h, int format, byte* dst)
    {
        NullGlState s = _s;
        if (!s.Active) { s.OrigReadPixels(fb, x, y, w, h, format, dst); return; }
        string? why = format != AuroraRgb ? $"format {format} is not the RGB the engine's snapshots ask for"
                    : dst == null || w <= 0 || h <= 0 ? "an empty read" : ReadComposite(s.Log!, x, y, w, h, dst);
        if (why != null && Fresh(7L << 32)) Say($"a snapshot read was not answered ({why}) -- the screenshot or save thumbnail is blank");
        else if (why == null && Fresh(8L << 32)) Say($"snapshots read Vulkan's composite ({w}x{h})");
    }

    private static string? ReadComposite(ExtenderLog log, int x, int y, int w, int h, byte* dst)
    {
        if (CX.Alloy.AlloyDevice.VulkanMade is not VkBackend vk) return "no Vulkan device yet";
        if (!vk.OnOwnThread) return "the device lives on another thread";
        ImageId comp = vk.Composite;
        if (comp.IsNull) return "no composite yet";
        if (!vk.DescOf(comp, out ImageDesc d)) return "the composite is stale";
        int cw = Math.Min(w, d.W - x), ch = Math.Min(h, d.H - y);
        if (x < 0 || y < 0 || cw <= 0 || ch <= 0) return $"the block is outside the {d.W}x{d.H} composite";
        BufferId buf = vk.CreateBuffer(new BufferDesc((long)cw * ch * 4, BufferUse.CopyDst, MemoryKind.Readback, "snapshot readback"));
        if (buf.IsNull) return "the readback buffer was refused";
        try
        {
            var cl = new CommandList("vulkan snapshot", 256, log);
            cl.CopyImageToBuffer(new CopyToBufferCmd(comp, new ImageRegion(0, x, y, 0), cw, ch, buf, 0));
            if (!vk.Submit(cl)) return "the copy's submit was refused";
            long at = vk.Submitted - 1;
            for (int waited = 0; !vk.Finished(at); waited++)
            {
                if (waited * 2 >= SnapshotWaitMs) return "the copy did not land in time";
                BlockingWait.Sleep(2, "a screenshot waits out its own readback, once");
            }
            byte[] rgba = new byte[cw * ch * 4];
            if (!vk.ReadMapped(buf, 0, rgba)) return "the readback was refused";
            int stride = (w * 3 + 3) & ~3;
            var to = new Span<byte>(dst, stride * h);
            for (int j = 0; j < ch; j++)
            {
                int row = j * stride, from = j * cw * 4;
                for (int i = 0; i < cw; i++)
                {
                    to[row + i * 3] = rgba[from + i * 4];
                    to[row + i * 3 + 1] = rgba[from + i * 4 + 1];
                    to[row + i * 3 + 2] = rgba[from + i * 4 + 2];
                }
            }
            return null;
        }
        finally { vk.Destroy(buf); }
    }
}

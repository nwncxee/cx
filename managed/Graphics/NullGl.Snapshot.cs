using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Graphics;

public sealed unsafe partial class NullGl
{
    /// <summary>Aurora's texture-format enum the engine's snapshots read in: entry 1 of <c>textureFormatTranslationTable</c>,
    /// GL_RGB with unsigned bytes.</summary>
    private const int AuroraRgb = 1;

    /// <summary>After every writer of the screen, the movie's last-before-present slot included.</summary>
    private const int SnapshotPhase = 95;

    /// <summary>A screenshot with no GL waits for the frame's screen: the main loop asks between presents, when Alloy
    /// has drawn nothing yet. A panorama renders its own faces and goes straight through.</summary>
    [UnmanagedCallersOnly]
    private static void OnSnapshot(byte* path, byte pano)
    {
        NullGlState s = _s;
        if (!s.Active || pano != 0 || path == null) { s.OrigSnapshot(path, pano); return; }
        var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(path);
        s.SnapshotPath = new byte[name.Length + 1];
        name.CopyTo(s.SnapshotPath);
    }

    /// <summary>The waiting screenshot, on the first frame with a screen to read.</summary>
    private static void TakeSnapshot()
    {
        NullGlState s = _s;
        if (s.SnapshotPath is not { } path || CX.Alloy.AlloyDevice.VulkanMade is not VkBackend { ScreenNow.IsNull: false }) return;
        s.SnapshotPath = null;
        fixed (byte* p = path) s.OrigSnapshot(p, 0);
    }

    /// <summary>Screenshots and save thumbnails with no GL: the screen the frame drew, read back before its present in
    /// the engine's GL_RGB layout -- rows bottom-up, each padded to 4 bytes. A format other than RGB, no device on this
    /// thread, or no screen drawn yet leaves the buffer as the engine allocated it and says so once.</summary>
    [UnmanagedCallersOnly]
    private static void OnReadPixels(uint fb, int x, int y, int w, int h, int format, byte* dst)
    {
        NullGlState s = _s;
        if (!s.Active) { s.OrigReadPixels(fb, x, y, w, h, format, dst); return; }
        string? why = format != AuroraRgb ? $"format {format} is not the RGB the engine's snapshots ask for"
                    : dst == null || w <= 0 || h <= 0 ? "an empty read" : ReadScreen(s.Log!, x, y, w, h, dst);
        if (why != null && Fresh(7L << 32)) Say($"a snapshot read was not answered ({why}) -- the screenshot or save thumbnail is blank");
        else if (why == null && Fresh(8L << 32)) Say($"snapshots read Vulkan's screen ({w}x{h})");
    }

    private static string? ReadScreen(ExtenderLog log, int x, int y, int w, int h, byte* dst)
    {
        if (CX.Alloy.AlloyDevice.VulkanMade is not VkBackend vk) return "no Vulkan device yet";
        if (!vk.OnOwnThread) return "the device lives on another thread";
        return ReadScreen(vk, log, x, y, w, h, dst);
    }

    /// <summary>Block <paramref name="x"/>, <paramref name="y"/> (GL's rows, from the bottom) of <paramref name="vk"/>'s
    /// screen into <paramref name="dst"/> as GL_RGB rows bottom-up; null when read, else why not.</summary>
    internal static string? ReadScreen(VkBackend vk, ExtenderLog log, int x, int y, int w, int h, byte* dst)
    {
        ImageId screen = vk.ScreenNow;
        if (screen.IsNull) return "nothing has drawn the screen this frame";
        if (!vk.DescOf(screen, out ImageDesc d)) return "the screen is stale";
        int cw = Math.Min(w, d.W - x), ch = Math.Min(h, d.H - y);
        if (x < 0 || y < 0 || cw <= 0 || ch <= 0) return $"the block is outside the {d.W}x{d.H} screen";
        (int top, _) = VkBackend.FlipRows(d.H, y, ch);
        (int r, int b) = d.Fmt == Format.Bgra8 ? (2, 0) : (0, 2);
        BufferId buf = vk.CreateBuffer(new BufferDesc((long)cw * ch * 4, BufferUse.CopyDst, MemoryKind.Readback, "snapshot readback"));
        if (buf.IsNull) return "the readback buffer was refused";
        try
        {
            var cl = new CommandList("vulkan snapshot", 256, log);
            cl.CopyImageToBuffer(new CopyToBufferCmd(screen, new ImageRegion(0, x, top, 0), cw, ch, buf, 0));
            if (!vk.Submit(cl)) return "the copy's submit was refused";
            if (!vk.WaitSubmitted()) return "the copy did not land in time";
            byte[] rgba = new byte[cw * ch * 4];
            if (!vk.ReadMapped(buf, 0, rgba)) return "the readback was refused";
            int stride = (w * 3 + 3) & ~3;
            var to = new Span<byte>(dst, stride * h);
            for (int j = 0; j < ch; j++)
            {
                int row = j * stride, from = (ch - 1 - j) * cw * 4;
                for (int i = 0; i < cw; i++)
                {
                    to[row + i * 3] = rgba[from + i * 4 + r];
                    to[row + i * 3 + 1] = rgba[from + i * 4 + 1];
                    to[row + i * 3 + 2] = rgba[from + i * 4 + b];
                }
            }
            return null;
        }
        finally { vk.Destroy(buf); }
    }
}

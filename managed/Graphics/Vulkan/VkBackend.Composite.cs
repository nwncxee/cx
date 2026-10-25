using System;
using System.Diagnostics;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend : CX.Alloy.PresentTarget.ISource
{
    /// <summary>Who holds a composite image between submits: GL (it may have read or written it since its last
    /// release, so it signals before Vulkan takes it), or nobody, GL having signalled the release Vulkan waits on.</summary>
    internal enum Holder : byte { Gl, Released }

    /// <summary>One composite image's hand-over between the APIs, as decisions only: which semaphore operations a
    /// take, a return and a flip owe. Every take waits once on GL's release, so GL signals exactly once per take.</summary>
    internal struct Handover
    {
        public Holder Holder;

        /// <summary>Vulkan takes the image for a submit; true = GL still holds it and must signal its release first.</summary>
        public bool Take() => Holder == Holder.Gl;

        /// <summary>The submit ran and GL waited on Vulkan's signal: GL holds the image again.</summary>
        public void Returned() => Holder = Holder.Gl;

        /// <summary>The image left the screen; true = GL signals its release now, ahead of Vulkan's next take.</summary>
        public bool Flip()
        {
            if (Holder != Holder.Gl) return false;
            Holder = Holder.Released;
            return true;
        }
    }

    /// <summary>A composite image's GL side: the import, its semaphore pair (Vulkan signals <see cref="VkDone"/> after
    /// a submit, GL <see cref="GlDone"/> on release; the <c>Gl*</c> names are the imports) and the hand-over.</summary>
    private sealed class Exported
    {
        public VkDeviceMemory Memory;
        public uint GlTex, GlMem;
        public VkSemaphore VkDone, GlDone;
        public uint GlVkDone, GlGlDone;
        public Handover Handover;
        /// <summary>Taken from GL in the submit being recorded.</summary>
        public bool Taken;
    }

    /// <summary>The composite pair: the front is the present image GL draws over and presents, the back the one the
    /// next frame renders into. Two, so a frame waits on GL's release of the image it showed two frames ago.</summary>
    private readonly ImageId[] _composite = new ImageId[2];
    private int _front;

    /// <summary>The image this frame renders into. Null id before the present target sized the pair.</summary>
    internal ImageId CompositeBack => _composite[_front ^ 1];

    /// <summary>Both composite images at <paramref name="w"/> x <paramref name="h"/>, RGBA8 to match the GL
    /// source: returns the front's GL name, 0 when either refused. The old pair dies after the frames in flight.</summary>
    public uint Resize(uint tex, int w, int h)
    {
        DropPair();
        if (!OnThread("Resize")) return 0;
        for (int i = 0; i < 2; i++)
        {
            VkImg? img = Export(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, w, h, 1, 1, 1, 1,
                                              ImageUse.ColorTarget | ImageUse.Sampled | ImageUse.CopySrc | ImageUse.CopyDst, $"composite {i}"));
            if (img == null) { DropPair(); return 0; }
            _composite[i] = new ImageId(_images.Add(img));
        }
        _front = 0;
        return Front;
    }

    /// <summary><paramref name="tex"/> is the pair's front, and the pair goes with it: neither image is wanted.</summary>
    public void Release(uint tex) => DropPair();

    private void DropPair()
    {
        for (int i = 0; i < 2; i++) { Destroy(_composite[i]); _composite[i] = default; }
    }

    /// <summary>Composite image <paramref name="i"/> (0 or 1, <see cref="_front"/> names one), null before a resize.</summary>
    private VkImg? Composite(int i) => _images.TryGet(_composite[i].Value, out VkImg g) ? g : null;

    private uint Front => Composite(_front)?.Ext!.GlTex ?? 0;

    /// <summary>The back image becomes the front, after a submit rendered it: GL releases the old front, which
    /// no GL pass touches again until it is the front once more. Returns the new front's GL name. In the 4.6 context.</summary>
    internal uint Flip()
    {
        if (!OnThread("Flip")) return 0;
        if (Composite(_front)?.Ext is Exported old && old.Handover.Flip()) GlHand(old, release: true);
        _front ^= 1;
        return Front;
    }

    /// <summary>GL's side of a hand-over, in the one layout both directions name: its release (a signal Vulkan's
    /// next take waits on), or its acquire (a wait on the signal Vulkan's submit raised).</summary>
    private static void GlHand(Exported e, bool release)
    {
        uint tex = e.GlTex, layout = GlInterop.LAYOUT_SHADER_READ_ONLY_EXT;
        var t = new ReadOnlySpan<uint>(in tex);
        var l = new ReadOnlySpan<uint>(in layout);
        if (release) GlInterop.Signal(e.GlGlDone, t, l);
        else GlInterop.Wait(e.GlVkDone, t, l);
    }

    /// <summary>A composite image: dedicated and exported, imported into the current GL context, with its own
    /// semaphore pair. Timed: the interop's per-image cost.</summary>
    private VkImg? Export(in ImageDesc d)
    {
        long t0 = Stopwatch.GetTimestamp();
        VkImageUsageFlags usage = Usage(d.Use, d.Fmt);
        if (!Exportable(Of(d.Fmt), usage)) { Fault($"image {d.Label}: {d.Fmt} cannot be exported with this usage"); return null; }
        var ext = new VkExternalMemoryImageCreateInfo { handleTypes = MemHandle };
        VkImageCreateInfo ici = ImageInfo(d, &ext);
        VkImage image;
        if (_vkd.vkCreateImage(&ici, &image) != VkResult.Success) { Fault($"image {d.Label}: vkCreateImage refused"); return null; }
        VkMemoryRequirements req;
        _vkd.vkGetImageMemoryRequirements(image, &req);
        int type = MemoryType(req.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal);
        // dedicated on BOTH sides: without it NVIDIA imports clean and every texel reads black
        var dedicated = new VkMemoryDedicatedAllocateInfo { image = image };
        var export = new VkExportMemoryAllocateInfo { pNext = &dedicated, handleTypes = MemHandle };
        var mai = new VkMemoryAllocateInfo { pNext = &export, allocationSize = req.size, memoryTypeIndex = (uint)type };
        VkDeviceMemory memory;
        if (type < 0 || _vkd.vkAllocateMemory(&mai, &memory) != VkResult.Success)
        {
            _vkd.vkDestroyImage(image);
            Fault($"image {d.Label}: no exportable device-local memory for {req.size} bytes");
            return null;
        }
        _vkd.vkBindImageMemory(image, memory, 0);
        IntPtr handle = ExportMemory(memory);
        uint glMem = 0;
        uint tex = handle == IntPtr.Zero ? 0 : GlInterop.ImportTexture(handle, req.size, GlFormats.Of(d.Fmt).Internal, d.W, d.H, out glMem);
        if (tex == 0)
        {
            _vkd.vkDestroyImage(image);
            _vkd.vkFreeMemory(memory);
            Fault($"image {d.Label}: GL refused the imported {d.Fmt} {d.W}x{d.H} allocation");
            return null;
        }
        var e = new Exported { Memory = memory, GlTex = tex, GlMem = glMem };
        try
        {
            (e.VkDone, e.GlVkDone) = SharedSemaphore();
            (e.GlDone, e.GlGlDone) = SharedSemaphore();
        }
        catch (InvalidOperationException x)
        {
            if (e.VkDone.IsNotNull) _vkd.vkDestroySemaphore(e.VkDone);
            GlInterop.DeleteSemaphore(e.GlVkDone);
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, tex);
            VkImage deadImage = image;
            VkDeviceMemory deadMemory = memory;
            uint deadGl = glMem;
            Later(() => { _vkd.vkDestroyImage(deadImage); _vkd.vkFreeMemory(deadMemory); }, () => GlInterop.DeleteMemory(deadGl));
            Fault($"image {d.Label}: its semaphore pair did not export -- {x.Message}");
            return null;
        }
        _log.Info($"VkBackend: composite image '{d.Label}' {d.W}x{d.H} {d.Fmt}, {req.size} bytes -- create + export + import "
                + $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F3} ms");
        return new VkImg { Image = image, View = View(image, d, -1), Desc = d, Ext = e };
    }
}

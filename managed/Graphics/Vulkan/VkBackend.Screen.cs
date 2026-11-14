using System;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The frame's screen: the swapchain's images as ids, the one taken this frame, and the acquire semaphores
    /// with the submit that last waited each.</summary>
    private sealed class ScreenState
    {
        public ImageId[] Ids = [];
        /// <summary>What <see cref="Screen()"/> answered this frame: a window image, the spare screen, or null before the first ask.</summary>
        public ImageId Now;
        /// <summary>The window image acquired this frame; -1 when none is.</summary>
        public int Index = -1;
        /// <summary>A recording moved the acquired image out of undefined, so its submit waits on the acquire.</summary>
        public bool Touched;
        /// <summary>The last acquire is not yet waited on by any submit.</summary>
        public bool Pending;
        public readonly VkSemaphore[] Acquire = new VkSemaphore[AcquireRing];
        /// <summary>The timeline value of the submit that waited each semaphore; long.MaxValue for one waited off it.</summary>
        public readonly long[] WaitedBy = new long[AcquireRing];
        public int Slot;
        /// <summary>The swap interval the last present asked for, which picks the next acquire's present mode.</summary>
        public int Interval = 1;
        public Format Fmt = Format.Bgra8;
    }

    private readonly ScreenState _scr = new();

    /// <summary>Acquire semaphores in turn: one more than the frames in flight, so an early acquire never reuses the
    /// one the oldest frame's submit may still be waiting on.</summary>
    private const int AcquireRing = FramesInFlight + 1;

    /// <summary>Where a submit waits on an acquire, and where a window image's move out of undefined starts: its first
    /// draw, or the spare screen's blit.</summary>
    private const VkPipelineStageFlags2 AcquireStages = VkPipelineStageFlags2.ColorAttachmentOutput | VkPipelineStageFlags2.AllTransfer;

    /// <summary>The image the frame's last writers draw, top row first in the window's format: this frame's window
    /// image, acquired on the first ask (which can wait for one to come free), or the spare screen while there is no
    /// window to draw or its size is not the frame's. Null before the present target sized it.</summary>
    internal ImageId Screen() => !_scr.Now.IsNull ? _scr.Now : _scr.Now = TakeScreen();

    /// <summary>The present target is sized: <see cref="Screen()"/> has an answer. Asks for no window image.</summary>
    internal bool HasScreen => !_spare.IsNull;

    /// <summary>The format every image <see cref="Screen()"/> answers is in; asks for no window image. Unknown before
    /// the present target sized it.</summary>
    internal Format ScreenFormat => HasScreen ? _scr.Fmt : Format.Unknown;

    /// <summary>What <see cref="Screen()"/> answered this frame, without asking; null when nothing has.</summary>
    internal ImageId ScreenNow => _scr.Now;

    /// <summary>The height of <paramref name="i"/> when it is a screen image (a window image or the spare screen), whose
    /// rows a draw reaches flipped; 0 for any other image.</summary>
    internal int FlipHeight(ImageId i) => !i.IsNull && _images.TryGet(i.Value, out VkImg img) && img.TopDown ? img.Desc.H : 0;

    /// <summary>This frame's window image, or the spare screen when there is no window, its size is not the frame's, or
    /// its images cannot be copied out of (a snapshot reads the screen back).</summary>
    private ImageId TakeScreen()
    {
        if (!OnThread("Screen") || !DescOf(_spare, out ImageDesc f)) return default;
        if (!EnsureSwapchain(Mode(_scr.Interval), f.W, f.H, _log) || _swap.W != f.W || _swap.H != f.H || !WindowReadable) return _spare;
        int idx = Acquire();
        if (idx < 0) return _spare;
        _scr.Index = idx;
        return _scr.Ids[idx];
    }

    /// <summary>The window's images can be copied out of, as their registered use says.</summary>
    private bool WindowReadable => _scr.Ids.Length > 0 && DescOf(_scr.Ids[0], out ImageDesc d) && (d.Use & ImageUse.CopySrc) != 0;

    /// <summary>The next window image into the next acquire semaphore, once the submit that last waited that semaphore
    /// has finished; -1 (logged) when the window gave none.</summary>
    private int Acquire()
    {
        ScreenState c = _scr;
        int slot = (c.Slot + 1) % AcquireRing;
        VkResult free = AcquireFree(slot);
        if (free != VkResult.Success) { Fault($"Present: the acquire semaphore never came free ({free})"); return -1; }
        c.WaitedBy[slot] = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        VkResult r = AcquireNext(c.Acquire[slot], out uint idx);
        if (r == VkResult.ErrorOutOfDateKHR)
        {
            _swap.Stale = true;
            if (!EnsureSwapchain(_swap.Mode, _swap.W, _swap.H, _log)) return -1;
            r = AcquireNext(c.Acquire[slot], out idx);
        }
        _acquireTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (r == VkResult.Timeout) SayStall(default, 0);
        if (r != VkResult.Success && r != VkResult.SuboptimalKHR) { Fault($"Present: vkAcquireNextImageKHR returned {r}"); return -1; }
        c.Slot = slot;
        c.Pending = true;
        if (_images.TryGet(c.Ids[idx].Value, out VkImg img)) { img.Layout = VkImageLayout.Undefined; img.Written = false; }
        return (int)idx;
    }

    /// <summary>The submit just queued waited on the acquire: its timeline value frees the semaphore for reuse.</summary>
    private void Waited()
    {
        _scr.Touched = _scr.Pending = false;
        _scr.WaitedBy[_scr.Slot] = _submits;
    }

    /// <summary>The frame is presented or dropped: the next ask takes a fresh screen.</summary>
    private void EndScreen()
    {
        _scr.Now = default;
        _scr.Index = -1;
    }

    /// <summary>The height of the screen image a pass with <paramref name="colors"/> draws first; 0 for any other target.</summary>
    private int FlipOf(ReadOnlySpan<ColorAttachment> colors)
    {
        foreach (ColorAttachment c in colors)
            if (!c.Image.IsNull) return FlipHeight(c.Image);
        return 0;
    }

    /// <summary>Rows <paramref name="y"/> up <paramref name="h"/> counted from the bottom of an image
    /// <paramref name="height"/> tall, as the rows from its top they cover, clipped at the top edge.</summary>
    internal static (int Y, int H) FlipRows(int height, int y, int h)
    {
        int top = Math.Max(height - (y + h), 0);
        return (top, Math.Max(height - y - top, 0));
    }

    /// <summary>The window's images as ids the passes target, top row first; replaces any from the last chain.</summary>
    private void RegisterWindowImages(VkImage[] images, Format f, int w, int h, bool readable)
    {
        DropWindowImages();
        var ids = new ImageId[images.Length];
        ImageUse use = ImageUse.ColorTarget | ImageUse.CopyDst | (readable ? ImageUse.CopySrc : ImageUse.None);
        for (int i = 0; i < images.Length; i++)
        {
            var d = new ImageDesc(ImageKind.Tex2D, f, w, h, 1, 1, 1, 1, use, $"window image {i}");
            ids[i] = new ImageId(_images.Add(new VkImg { Image = images[i], View = View(images[i], d, -1), Desc = d, TopDown = true, Window = true }));
        }
        _scr.Ids = ids;
    }

    /// <summary>The window images' ids and views. The caller has waited the device idle.</summary>
    private void DropWindowImages()
    {
        foreach (ImageId id in _scr.Ids)
            if (_images.Remove(id.Value, out VkImg g)) FreeViews(g.View, g.MipViews, g.LayerViews);
        if (_scr.Ids.Length > 0) _sets.Stale = true;
        _scr.Ids = [];
        if (_scr.Index >= 0) EndScreen();
    }

    /// <summary>The seam's name for the window format <paramref name="f"/>; BGRA when the window offers neither.</summary>
    private static Format ScreenFormatOf(VkFormat f) => f == VkFormat.R8G8B8A8Unorm ? Format.Rgba8 : Format.Bgra8;
}

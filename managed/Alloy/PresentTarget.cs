using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>Where Alloy's frame lands: Vulkan's composite image, sized to the window here and presented by Vulkan
/// itself. The world frame marks it composed; the GUI flush that draws over it hands it back.</summary>
internal static class PresentTarget
{
    /// <summary>The composite's size, and whether the world frame has composited into it since the last hand-back.</summary>
    private sealed class PresentState
    {
        public int W, H;
        public bool Composed, Announced;
        public long HandBacks;
    }

    private static PresentState _s = new();
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The framebuffer GL code would bind for the screen. ponytail: GL paths still compiled in bind 0;
    /// none runs on Vulkan, and they go with the GL backend.</summary>
    internal static uint Fbo46 => 0;

    /// <summary>Is there a GL target for a frame to land on? Never: the frame is Vulkan's.</summary>
    internal static bool Usable => false;

    /// <summary>The composite's size.</summary>
    internal static int Width => _s.W;
    /// <inheritdoc cref="Width"/>
    internal static int Height => _s.H;

    /// <summary>Size Vulkan's composite to the window, once per size; a refusal fails Alloy, said once.</summary>
    internal static void Ensure(int w, int h)
    {
        PresentState s = _s;
        if (w <= 0 || h <= 0 || (w == s.W && h == s.H)) return;
        s.W = w; s.H = h;
        if (_log == null || AlloyDevice.Vulkan(_log) is not VkBackend vk || !vk.Resize(w, h))
            AlloyGate.Fail($"Vulkan's {w}x{h} composite image was refused (logged above)");
        else if (!s.Announced) { s.Announced = true; _log.Info($"PresentTarget: Vulkan composites into its own {w}x{h} image and presents it"); }
    }

    /// <summary>The world frame is about to composite into the image: the GUI flush draws over it as it is.</summary>
    internal static void MarkComposed() => _s.Composed = true;

    /// <summary>Has the world composited this frame? Its frame then ran the deferred work already.</summary>
    internal static bool Composed => _s.Composed;

    /// <summary>The frame is finished: the next GUI flush starts a new one.</summary>
    internal static void HandBack() { _s.Composed = false; _s.HandBacks++; }

    /// <summary><see cref="HandBack"/> when the world composited and nothing has handed it back yet.</summary>
    internal static void HandBackIfOwed() { if (_s.Composed) HandBack(); }

    /// <summary>The composite's size and its hand-backs this session.</summary>
    internal static string Stats()
        => _s.W == 0 ? "present: no composite image yet" : $"present: {_s.W}x{_s.H} Vulkan composite, {_s.HandBacks} hand-backs";

    /// <summary>A lost device takes the composite with it; the next <see cref="Ensure"/> sizes a fresh one.</summary>
    internal static void ContextLost() => _s = new PresentState();
}

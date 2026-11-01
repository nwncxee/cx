using System.Runtime.InteropServices;
using CX.Rhi;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The device as native/ffx/cxfsr.cpp takes it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FsrDevice
    {
        public VkInstance Instance;
        public void* Gipa;
        public VkPhysicalDevice Physical;
        public VkDevice Device;
        public void* Gdpa;
    }

    /// <summary>One image of a dispatch: the output (<see cref="Uav"/> 1) in GENERAL, every input in SHADER_READ_ONLY.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FsrImage
    {
        public VkImage Image;
        public VkFormat Format;
        public uint W, H, Uav;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FsrDispatch
    {
        public VkCommandBuffer Cmd;
        public FsrImage Color, Depth, Motion, Exposure, Output;
        public float JitterX, JitterY, MvScaleX, MvScaleY;
        public uint RenderW, RenderH;
        public float FrameMs, Near, Far, FovY;
        public uint Reset;
    }

    [DllImport("cxfsr")] private static extern int cxfsr_create(FsrDevice* dev, uint maxW, uint maxH, uint displayW, uint displayH, nint* ctx);
    [DllImport("cxfsr")] private static extern int cxfsr_ring();
    [DllImport("cxfsr")] private static extern int cxfsr_phase_count(int renderW, int displayW);

    /// <summary>FSR's jitter sequence length for a <paramref name="renderW"/>-wide frame on a <paramref name="displayW"/>
    /// display, as its library counts it. Touches no device.</summary>
    internal static int FsrPhaseCount(int renderW, int displayW) => cxfsr_phase_count(renderW, displayW);
    [DllImport("cxfsr")] private static extern int cxfsr_dispatch(nint ctx, FsrDispatch* d);
    [DllImport("cxfsr")] private static extern ulong cxfsr_vram(nint ctx);
    [DllImport("cxfsr")] private static extern void cxfsr_destroy(nint ctx);

    /// <summary>The device's FSR context and the sizes it was made for; a refusal holds for the device's life.</summary>
    private sealed class FsrState
    {
        public nint Ctx;
        public int MaxW, MaxH, DisplayW, DisplayH;
        public string? Refused;
        /// <summary>The timeline value each of the last ring's worth of dispatches finishes at, by dispatch number: FSR's
        /// backend rings its constants and descriptor sets, so a dispatch rewrites what the one a ring back used.</summary>
        public readonly long[] Ring = new long[cxfsr_ring()];
        public long Dispatches;
    }

    private readonly FsrState _fsr = new();

    /// <summary>Is FSR ready on this device for frames up to <paramref name="maxW"/> x <paramref name="maxH"/> upscaled
    /// to <paramref name="displayW"/> x <paramref name="displayH"/>? Null when it is; otherwise why not. A new size
    /// replaces the context, the old one freed once no submit can reach it.</summary>
    internal string? FsrReady(int maxW, int maxH, int displayW, int displayH)
    {
        FsrState f = _fsr;
        if (f.Refused != null) return f.Refused;
        if (f.Ctx != 0 && f.MaxW == maxW && f.MaxH == maxH && f.DisplayW == displayW && f.DisplayH == displayH) return null;
        if (f.Ctx != 0)
        {
            nint old = f.Ctx;
            Later(() => cxfsr_destroy(old));
            f.Ctx = 0;
        }
        var dev = new FsrDevice
        {
            Instance = _inst, Gipa = vkGetInstanceProcAddr(_inst, "vkGetInstanceProcAddr"u8), Physical = _phys, Device = _device,
            Gdpa = vkGetInstanceProcAddr(_inst, "vkGetDeviceProcAddr"u8),
        };
        nint ctx;
        int e = cxfsr_create(&dev, (uint)maxW, (uint)maxH, (uint)displayW, (uint)displayH, &ctx);
        if (e != 0)
        {
            f.Refused = $"FSR 3.1 refused this device: creating its context returned FfxErrorCode {e}";
            _log.Always($"VkBackend: {f.Refused}");
            return f.Refused;
        }
        (f.Ctx, f.MaxW, f.MaxH, f.DisplayW, f.DisplayH) = (ctx, maxW, maxH, displayW, displayH);
        _log.Always($"VkBackend: FSR 3.1 ready, up to {maxW}x{maxH} into {displayW}x{displayH}, {cxfsr_vram(ctx) / (1024.0 * 1024.0):F0} MiB of its own");
        return null;
    }

    /// <summary>The frame's size: the window a full-screen view covers.</summary>
    internal (int W, int H) FrameSize => _images.TryGet(_standIn.Value, out VkImg c) ? (c.Desc.W, c.Desc.H) : (0, 0);

    /// <summary>The device's FSR context goes with it.</summary>
    private void DisposeFsr()
    {
        if (_fsr.Ctx != 0) cxfsr_destroy(_fsr.Ctx);
        _fsr.Ctx = 0;
    }

    /// <summary>One FSR dispatch, recorded straight into the frame's command buffer: the inputs to SHADER_READ_ONLY and
    /// the output to GENERAL first, which is where FSR leaves them again.</summary>
    private bool Upscale(UpscaleCmd c)
    {
        if (_inPass) return Fault("Upscale inside a render pass");
        if (_onAsync) return Fault("Upscale on the async compute queue");
        if (_fsr.Ctx == 0) return Fault("Upscale with no FSR context -- FsrReady was not asked, or refused");
        if (!_images.TryGet(c.Color.Value, out VkImg color) || !_images.TryGet(c.Depth.Value, out VkImg depth)
            || !_images.TryGet(c.Motion.Value, out VkImg motion) || !_images.TryGet(c.Output.Value, out VkImg output))
            return Fault("Upscale: a stale image");
        VkImg? exposure = null;
        if (!c.Exposure.IsNull && !_images.TryGet(c.Exposure.Value, out exposure)) return Fault("Upscale: a stale exposure image");
        if ((output.Desc.Use & ImageUse.Storage) == 0) return Fault($"Upscale into {output.Desc.Label}, which is not a storage image");
        Transition(color, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(depth, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(motion, VkImageLayout.ShaderReadOnlyOptimal, false);
        if (exposure != null) Transition(exposure, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(output, VkImageLayout.General);
        Flush();
        long slot = _fsr.Dispatches % _fsr.Ring.Length;
        if (WaitFor(_fsr.Ring[slot]) != VkResult.Success) return Fault("Upscale: the dispatch whose FSR constants this one reuses never finished");
        var d = new FsrDispatch
        {
            Cmd = _cmd, Color = Image(color, 0), Depth = Image(depth, 0), Motion = Image(motion, 0),
            Exposure = exposure == null ? default : Image(exposure, 0), Output = Image(output, 1),
            JitterX = c.JitterX, JitterY = c.JitterY, MvScaleX = c.MvScaleX, MvScaleY = c.MvScaleY,
            RenderW = (uint)c.RenderW, RenderH = (uint)c.RenderH, FrameMs = c.FrameMs, Near = c.Near, Far = c.Far, FovY = c.FovY,
            Reset = c.Reset ? 1u : 0u,
        };
        int e = cxfsr_dispatch(_fsr.Ctx, &d);
        _fsr.Ring[slot] = Next;
        _fsr.Dispatches++;
        output.Written = true;
        // FSR bound its own pipelines and sets on the compute point, which disturbs the bindless set there
        BindBindless();
        return e == 0 || Fault($"Upscale: FSR's dispatch returned FfxErrorCode {e}");
    }

    private static FsrImage Image(VkImg i, uint uav) => new() { Image = i.Image, Format = Of(i.Desc.Fmt), W = (uint)i.Desc.W, H = (uint)i.Desc.H, Uav = uav };
}

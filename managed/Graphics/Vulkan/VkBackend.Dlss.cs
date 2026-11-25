using System;
using System.Runtime.InteropServices;
using CX.Rhi;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>NVIDIA DLSS Super Resolution through native/dlss/cxdlss.cpp (Windows only): the same
/// <see cref="UpscaleCmd"/> FSR takes, dispatched to NGX when <see cref="DlssReady"/> was the last of the two asked.</summary>
internal sealed unsafe partial class VkBackend
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DlssImage
    {
        public VkImage Image;
        public VkImageView View;
        public VkFormat Format;
        public uint W, H;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlssDispatch
    {
        public VkCommandBuffer Cmd;
        public DlssImage Color, Depth, Motion, Output;
        public float JitterX, JitterY, MvScaleX, MvScaleY;
        public uint RenderW, RenderH;
        public float FrameMs;
        public uint Reset;
    }

    [DllImport("cxdlss")] private static extern int cxdlss_required(uint* instN, byte*** inst, uint* devN, byte*** dev);
    [DllImport("cxdlss")] private static extern int cxdlss_init(FsrDevice* dev, char* dataPath);
    [DllImport("cxdlss")] private static extern int cxdlss_create(VkCommandBuffer cmd, uint maxW, uint maxH, uint displayW, uint displayH, nint* ctx);
    [DllImport("cxdlss")] private static extern int cxdlss_dispatch(nint ctx, DlssDispatch* d);
    [DllImport("cxdlss")] private static extern void cxdlss_destroy(nint ctx);
    [DllImport("cxdlss")] private static extern void cxdlss_shutdown();

    /// <summary>The device was opened with NGX's extensions: on Windows, on an NVIDIA GPU that offers them. They cost
    /// nothing unused, so they go on whatever is picked and DLSS needs no restart.</summary>
    private bool _ngxOpened;

    /// <summary>Why NGX's extensions are not on this device, for the log and for <see cref="DlssReady"/>'s refusal.</summary>
    private static string _ngxWhy = "the device has not been opened";

    /// <summary>NVIDIA's PCI vendor id: NGX's extensions are asked for only on its devices.</summary>
    private const uint NvidiaVendor = 0x10DE;

    /// <summary>The device's DLSS context and the sizes it was made for; a refusal holds for the device's life.</summary>
    private sealed class DlssState
    {
        public nint Ctx;
        public bool Up, Active;
        public readonly SizeSettle Settle = new();
        public int MaxW, MaxH, DisplayW, DisplayH;
        public string? Refused;
    }

    private readonly DlssState _dlss = new();

    /// <summary>NGX's instance (<paramref name="instance"/> true) or device extension names; none off Windows or when the
    /// wrapper is not linked in, with <see cref="_ngxWhy"/> saying which.</summary>
    private static string[] NgxExtensions(bool instance)
    {
        if (!Win) { _ngxWhy = "DLSS is Windows-only in this build"; return []; }
        try
        {
            uint ni = 0, nd = 0;
            byte** i = null, d = null;
            int e = cxdlss_required(&ni, &i, &nd, &d);
            if (e != 0) { _ngxWhy = $"NGX would not list its extensions (NVSDK_NGX_Result 0x{e:x8})"; return []; }
            uint n = instance ? ni : nd;
            byte** list = instance ? i : d;
            var names = new string[n];
            for (int k = 0; k < n; k++) names[k] = Marshal.PtrToStringAnsi((nint)list[k]) ?? "";
            return names;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _ngxWhy = "this build was made without NVIDIA's DLSS SDK";
            return [];
        }
    }

    /// <summary>Is DLSS ready on this device for frames up to <paramref name="maxW"/> x <paramref name="maxH"/> upscaled
    /// to <paramref name="displayW"/> x <paramref name="displayH"/>? Null when it is; otherwise why not. The context
    /// itself is made at the first dispatch, on the frame's own command buffer.</summary>
    internal string? DlssReady(int maxW, int maxH, int displayW, int displayH)
    {
        DlssState s = _dlss;
        if (s.Refused != null) return s.Refused;
        if (!_ngxOpened) return s.Refused = _ngxWhy;
        if (!s.Up)
        {
            FsrDevice dev = FfxDevice();
            int e;
            fixed (char* path = System.IO.Path.GetTempPath() + "\0") e = cxdlss_init(&dev, path);
            if (e != 0)
            {
                s.Refused = e switch
                {
                    1 => "this GPU or driver does not offer DLSS (an NVIDIA RTX card is needed)",
                    2 => "the NVIDIA driver is too old for this DLSS -- update it",
                    _ => $"NGX would not start on this device (NVSDK_NGX_Result 0x{e:x8})",
                };
                _log.Always($"VkBackend: DLSS refused: {s.Refused}");
                return s.Refused;
            }
            s.Up = true;
            _log.Always("VkBackend: NGX up, DLSS Super Resolution available");
        }
        s.Active = true;
        if (s.Ctx != 0 && (s.MaxW, s.MaxH, s.DisplayW, s.DisplayH) == (maxW, maxH, displayW, displayH)) { s.Settle.Clear(); return null; }
        if (s.Ctx != 0)
        {
            if (!s.Settle.Held((maxW, maxH, displayW, displayH))) return Settling;
            nint old = s.Ctx;
            Later(() => cxdlss_destroy(old));
            s.Ctx = 0;
        }
        (s.MaxW, s.MaxH, s.DisplayW, s.DisplayH) = (maxW, maxH, displayW, displayH);
        return null;
    }

    /// <summary>The DLSS context and NGX go with the device.</summary>
    private void DisposeDlss()
    {
        if (!_dlss.Up) return;
        if (_dlss.Ctx != 0) cxdlss_destroy(_dlss.Ctx);
        _dlss.Ctx = 0;
        cxdlss_shutdown();
        _dlss.Up = false;
    }

    /// <summary>One DLSS dispatch into the frame's command buffer, making the context first when the sizes are new:
    /// the inputs in SHADER_READ_ONLY and the output in GENERAL, as FSR takes them.</summary>
    private bool UpscaleDlss(UpscaleCmd c)
    {
        DlssState s = _dlss;
        if (!_images.TryGet(c.Color.Value, out VkImg color) || !_images.TryGet(c.Depth.Value, out VkImg depth)
            || !_images.TryGet(c.Motion.Value, out VkImg motion) || !_images.TryGet(c.Output.Value, out VkImg output))
            return Fault("Upscale: a stale image");
        if ((output.Desc.Use & ImageUse.Storage) == 0) return Fault($"Upscale into {output.Desc.Label}, which is not a storage image");
        Transition(color, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(depth, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(motion, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(output, VkImageLayout.General);
        Flush();
        if (s.Ctx == 0)
        {
            nint ctx;
            int ce = cxdlss_create(_cmd, (uint)s.MaxW, (uint)s.MaxH, (uint)s.DisplayW, (uint)s.DisplayH, &ctx);
            if (ce != 0)
            {
                s.Refused = $"creating its context returned NVSDK_NGX_Result 0x{ce:x8}";
                _log.Always($"VkBackend: DLSS refused: {s.Refused}");
                BindBindless();
                return true;   // the frame keeps its un-upscaled colour this once; the next asks FSR
            }
            s.Ctx = ctx;
            _log.Always($"VkBackend: DLSS ready, up to {s.MaxW}x{s.MaxH} into {s.DisplayW}x{s.DisplayH}");
        }
        var d = new DlssDispatch
        {
            Cmd = _cmd, Color = Dlss(color), Depth = Dlss(depth), Motion = Dlss(motion), Output = Dlss(output),
            JitterX = c.JitterX, JitterY = c.JitterY, MvScaleX = c.MvScaleX, MvScaleY = c.MvScaleY,
            RenderW = (uint)c.RenderW, RenderH = (uint)c.RenderH, FrameMs = c.FrameMs, Reset = c.Reset ? 1u : 0u,
        };
        int e = cxdlss_dispatch(s.Ctx, &d);
        output.Written = true;
        // NGX bound its own pipelines and sets on the compute point, which disturbs the bindless set there
        BindBindless();
        return e == 0 || Fault($"Upscale: DLSS's dispatch returned NVSDK_NGX_Result 0x{e:x8}");
    }

    /// <summary>The upscaler refused at its first dispatch, for the post stack to stand it down and say why.</summary>
    internal string? DlssRefusal => _dlss.Refused;

    private static DlssImage Dlss(VkImg i) => new() { Image = i.Image, View = i.View, Format = Of(i.Desc.Fmt), W = (uint)i.Desc.W, H = (uint)i.Desc.H };
}

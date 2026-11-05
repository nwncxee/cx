using System.Runtime.InteropServices;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>One dispatch as native/ffx/cxfsr.cpp takes it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SssrDispatch
    {
        public VkCommandBuffer Cmd;
        public FsrImage Color, Depth, Motion, Normal, Env, Output;
        public uint EnvMips;
        public System.Numerics.Matrix4x4 InvVp, Proj, InvProj, View, InvView, PrevVp;
        public float MvScaleX, MvScaleY, Thickness, Stability, RoughnessCap, Variance;
        public uint MaxIterations, MinOccupancy, SamplesPerQuad;
        public System.Numerics.Vector3 ProbeAt;
    }

    [DllImport("cxfsr")] private static extern int cxsssr_create(FsrDevice* dev, uint w, uint h, nint* ctx);
    [DllImport("cxfsr")] private static extern int cxsssr_dispatch(nint ctx, SssrDispatch* d);
    [DllImport("cxfsr")] private static extern void cxsssr_destroy(nint ctx);

    /// <summary>AMD's sample values: a ray's lookups in the hierarchy, the lanes under which its loop stops early, and the
    /// luminance difference between history results that asks for one more ray.</summary>
    private const uint SssrMaxIterations = 128, SssrMinOccupancy = 4;
    private const float SssrVariance = 0f;

    /// <summary>The device's SSSR context and the size it was made for; a refusal holds for the device's life.</summary>
    private sealed class SssrContextState
    {
        public nint Ctx;
        public int W, H;
        public string? Refused;
        /// <summary>As <see cref="FsrState.Ring"/>: its backend rings its constants and descriptor sets the same way.</summary>
        public readonly long[] Ring = new long[cxffx_ring()];
        public long Dispatches;
    }

    private readonly SssrContextState _sssr = new();

    /// <summary>Is FidelityFX SSSR ready for <paramref name="w"/> x <paramref name="h"/> frames? Null when it is; otherwise why
    /// not. A new size replaces the context, the old one freed once no submit can reach it.</summary>
    internal string? SssrReady(int w, int h)
    {
        SssrContextState s = _sssr;
        if (s.Refused != null) return s.Refused;
        if (s.Ctx != 0 && s.W == w && s.H == h) return null;
        if (s.Ctx != 0)
        {
            nint old = s.Ctx;
            Later(() => cxsssr_destroy(old));
            s.Ctx = 0;
        }
        FsrDevice dev = FfxDevice();
        nint ctx;
        int e = cxsssr_create(&dev, (uint)w, (uint)h, &ctx);
        if (e != 0) return s.Refused = $"creating its context returned FfxErrorCode {e}";
        (s.Ctx, s.W, s.H) = (ctx, w, h);
        _log.Always($"VkBackend: SSSR ready at {w}x{h}");
        return null;
    }

    /// <summary>The device's SSSR context goes with it.</summary>
    private void DisposeSssr()
    {
        if (_sssr.Ctx != 0) cxsssr_destroy(_sssr.Ctx);
        _sssr.Ctx = 0;
    }

    /// <summary>One SSSR dispatch, recorded straight into the frame's command buffer: the inputs to SHADER_READ_ONLY and the
    /// output to GENERAL first, which is where AMD's backend leaves them again.</summary>
    private bool Sssr(SssrCmd c)
    {
        if (_inPass) return Fault("Sssr inside a render pass");
        if (_onAsync) return Fault("Sssr on the async compute queue");
        if (_sssr.Ctx == 0) return Fault("Sssr with no context -- SssrReady was not asked, or refused");
        if (!_images.TryGet(c.Color.Value, out VkImg color) || !_images.TryGet(c.Depth.Value, out VkImg depth)
            || !_images.TryGet(c.Motion.Value, out VkImg motion) || !_images.TryGet(c.Normal.Value, out VkImg normal)
            || !_images.TryGet(c.Env.Value, out VkImg env) || !_images.TryGet(c.Output.Value, out VkImg output))
            return Fault("Sssr: a stale image");
        Transition(color, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(depth, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(motion, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(normal, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(env, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(output, VkImageLayout.General);
        long slot = _sssr.Dispatches % _sssr.Ring.Length;
        if (!FfxRingWait(_sssr.Ring, slot)) return Fault("Sssr: the dispatch whose constants this one reuses never finished");
        var d = new SssrDispatch
        {
            Cmd = _cmd, Color = Image(color, 0), Depth = Image(depth, 0), Motion = Image(motion, 0), Normal = Image(normal, 0),
            Env = Image(env, 0), Output = Image(output, 1), EnvMips = (uint)env.Desc.Mips,
            InvVp = c.InvVp, Proj = c.Proj, InvProj = c.InvProj, View = c.View, InvView = c.InvView, PrevVp = c.PrevVp,
            MvScaleX = c.MvScaleX, MvScaleY = c.MvScaleY, Thickness = c.Thickness, Stability = c.Stability, RoughnessCap = c.RoughnessCap,
            Variance = SssrVariance, MaxIterations = SssrMaxIterations, MinOccupancy = SssrMinOccupancy, SamplesPerQuad = (uint)c.SamplesPerQuad,
            ProbeAt = c.ProbeAt,
        };
        int e = cxsssr_dispatch(_sssr.Ctx, &d);
        _sssr.Ring[slot] = Next;
        _sssr.Dispatches++;
        output.Written = true;
        BindBindless();
        return e == 0 || Fault($"Sssr: AMD's dispatch returned FfxErrorCode {e}");
    }
}

using System.Runtime.InteropServices;
using CX.Rhi;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>One dispatch as native/ffx/cxfsr.cpp takes it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DofDispatch
    {
        public VkCommandBuffer Cmd;
        public FsrImage Color, Depth, Output;
        public float CocScale, FocusNear, FocusFar, DepthA, DepthB, DepthMin, DepthMax;
    }

    [DllImport("cxfsr")] private static extern int cxdof_create(FsrDevice* dev, uint w, uint h, uint rings, float cocLimitFactor, nint* ctx);
    [DllImport("cxfsr")] private static extern int cxdof_dispatch(nint ctx, DofDispatch* d);
    [DllImport("cxfsr")] private static extern void cxdof_destroy(nint ctx);

    /// <summary>The kernel's rings; past them the blur reads a coarser mip. About 130 samples at each half-size pixel it touches.</summary>
    private const uint DofRings = 6;
    /// <summary>The blur's limit as a fraction of the frame's height, full-size pixels of radius being half of it again in AMD's terms.</summary>
    private const float DofCocLimitFactor = 0.016f;

    /// <summary>The device's DoF context and the size it was made for; a refusal holds for the device's life.</summary>
    private sealed class DofContextState
    {
        public nint Ctx;
        public int W, H;
        public string? Refused;
        /// <summary>As <see cref="FsrState.Ring"/>: its backend rings its constants and descriptor sets the same way.</summary>
        public readonly long[] Ring = new long[cxfsr_ring()];
        public long Dispatches;
    }

    private readonly DofContextState _dof = new();

    /// <summary>Why a device reporting these features cannot run the effect, or null. AMD's blur asks the subgroup's lanes
    /// to vote and to agree on a maximum, and its depth is read through a filtering sampler.</summary>
    internal static string? DofRefusalOf(bool waves, bool linearDepth)
        => !waves ? "this device has no subgroup vote and arithmetic operations in compute"
         : !linearDepth ? "this device cannot filter its depth format"
         : null;

    /// <summary><see cref="DofRefusalOf"/> as this device answers it.</summary>
    private string? DofDeviceRefusal()
    {
        var p11 = new VkPhysicalDeviceVulkan11Properties();
        var p2 = new VkPhysicalDeviceProperties2 { pNext = &p11 };
        _vki.vkGetPhysicalDeviceProperties2(_phys, &p2);
        const VkSubgroupFeatureFlags want = VkSubgroupFeatureFlags.Basic | VkSubgroupFeatureFlags.Vote | VkSubgroupFeatureFlags.Arithmetic;
        bool waves = (p11.subgroupSupportedOperations & want) == want && (p11.subgroupSupportedStages & VkShaderStageFlags.Compute) != 0;
        _vki.vkGetPhysicalDeviceFormatProperties(_phys, Of(Format.D32F), out VkFormatProperties f);
        return DofRefusalOf(waves, (f.optimalTilingFeatures & VkFormatFeatureFlags.SampledImageFilterLinear) != 0);
    }

    /// <summary>Is FidelityFX DoF ready on this device for <paramref name="w"/> x <paramref name="h"/> frames? Null when
    /// it is; otherwise why not. A new size replaces the context, the old one freed once no submit can reach it.</summary>
    internal string? DofReady(int w, int h)
    {
        DofContextState d = _dof;
        if (d.Refused != null) return d.Refused;
        if (d.Ctx != 0 && d.W == w && d.H == h) return null;
        if (d.Ctx != 0)
        {
            nint old = d.Ctx;
            Later(() => cxdof_destroy(old));
            d.Ctx = 0;
        }
        else if (DofDeviceRefusal() is string no) return d.Refused = no;
        var dev = new FsrDevice
        {
            Instance = _inst, Gipa = vkGetInstanceProcAddr(_inst, "vkGetInstanceProcAddr"u8), Physical = _phys, Device = _device,
            Gdpa = vkGetInstanceProcAddr(_inst, "vkGetDeviceProcAddr"u8),
        };
        nint ctx;
        int e = cxdof_create(&dev, (uint)w, (uint)h, DofRings, DofCocLimitFactor, &ctx);
        if (e != 0) return d.Refused = $"creating its context returned FfxErrorCode {e}";
        (d.Ctx, d.W, d.H) = (ctx, w, h);
        _log.Always($"VkBackend: depth of field ready at {w}x{h}");
        return null;
    }

    /// <summary>The device's DoF context goes with it.</summary>
    private void DisposeDof()
    {
        if (_dof.Ctx != 0) cxdof_destroy(_dof.Ctx);
        _dof.Ctx = 0;
    }

    /// <summary>One DoF dispatch, recorded straight into the frame's command buffer: the inputs to SHADER_READ_ONLY and
    /// the output to GENERAL first, which is where AMD's backend leaves them again.</summary>
    private bool DepthOfField(DofCmd c)
    {
        if (_inPass) return Fault("DepthOfField inside a render pass");
        if (_onAsync) return Fault("DepthOfField on the async compute queue");
        if (_dof.Ctx == 0) return Fault("DepthOfField with no context -- DofReady was not asked, or refused");
        if (!_images.TryGet(c.Color.Value, out VkImg color) || !_images.TryGet(c.Depth.Value, out VkImg depth)
            || !_images.TryGet(c.Output.Value, out VkImg output))
            return Fault("DepthOfField: a stale image");
        if ((output.Desc.Use & ImageUse.Storage) == 0) return Fault($"DepthOfField into {output.Desc.Label}, which is not a storage image");
        Transition(color, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(depth, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(output, VkImageLayout.General);
        Flush();
        long slot = _dof.Dispatches % _dof.Ring.Length;
        if (WaitFor(_dof.Ring[slot]) != VkResult.Success) return Fault("DepthOfField: the dispatch whose constants this one reuses never finished");
        var d = new DofDispatch
        {
            Cmd = _cmd, Color = Image(color, 0), Depth = Image(depth, 0), Output = Image(output, 1),
            CocScale = c.CocScale, FocusNear = c.FocusNear, FocusFar = c.FocusFar, DepthA = c.DepthA, DepthB = c.DepthB,
            DepthMin = c.DepthMin, DepthMax = c.DepthMax,
        };
        int e = cxdof_dispatch(_dof.Ctx, &d);
        _dof.Ring[slot] = Next;
        _dof.Dispatches++;
        output.Written = true;
        // AMD's backend bound its own pipelines and sets on the compute point, which disturbs the bindless set there
        BindBindless();
        return e == 0 || Fault($"DepthOfField: AMD's dispatch returned FfxErrorCode {e}");
    }
}

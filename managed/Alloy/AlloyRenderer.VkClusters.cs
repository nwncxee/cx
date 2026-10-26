using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The froxel build on Vulkan: the same stage GL dispatches, over the same light records and the same
/// terms that build sent, into this device's own cluster table. It is the list the deferred record writer picks
/// its shadow candidates from, so without it that record is empty and every deferred light goes unshadowed.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The build's resources on one device, replaced whole with the device.</summary>
    private sealed class VkClusterState
    {
        public VkBackend? Dev;
        public bool Failed, Live;
        public VkDefStage? Build;
        /// <summary>The froxel heads, the index pool, the mover spheres the hot partition reads, and the build's
        /// own overflow counters.</summary>
        public BufferId Counts, Idx, Movers, Stats;
        public long MoverCap;
        public int MoversGen = -1;
        /// <summary>This frame's table is this view's: a frame whose build did not run leaves the readers on the
        /// zero stand-in rather than on another camera's binning.</summary>
        public bool Fresh;
    }

    private static VkClusterState _vkcl = new();

    /// <summary>Head words a froxel takes, as <c>cx_lightbuild</c> writes them.</summary>
    private static int VkClusterHeads => AlloyLights.ClusterHeadWords;

    /// <summary>The froxel table this frame, or a null id when the build did not run.</summary>
    private static BufferId VkClusterCounts => _vkcl.Fresh ? _vkcl.Counts : default;
    private static BufferId VkClusterIdx => _vkcl.Fresh ? _vkcl.Idx : default;
    private static BufferId VkMovers => _vkcl.Movers;

    /// <summary>The build into <paramref name="cl"/>, ahead of everything that reads a froxel list. Records nothing
    /// (and leaves the readers on the stand-in) when the GL rig did not build this frame -- a probe capture, a
    /// stood-down rig -- or when the device refused the stage.</summary>
    internal static void VkClusterBuild(VkBackend vk, CommandList cl, ExtenderLog log)
    {
        VkClusterState c = _vkcl;
        if (c.Dev != vk) _vkcl = c = new VkClusterState { Dev = vk };
        c.Fresh = false;
        AlloyLights.BuildTerms t = AlloyLights.Terms;
        if (c.Failed || !t.Ran || !AlloyLights.Ready) return;
        if (!VkClusterEnsure(vk, cl, log)) return;
        // the records the build bins, uploaded here rather than at the deferred pass: this dispatch is the first
        // reader of them in the frame, and a bind of a buffer that does not exist yet faults the whole recording
        if (!VkDefLights(vk, cl, System.Runtime.InteropServices.MemoryMarshal.AsBytes(AlloyLights.Staged), log)
            || VkDeferredLights().IsNull) return;
        ReadOnlySpan<byte> spheres = System.Runtime.InteropServices.MemoryMarshal.AsBytes(CasterPass.CubeDynSphere.AsSpan());
        if (c.MoversGen != CasterPass.CubeDynSphereGen)
        {
            c.MoversGen = CasterPass.CubeDynSphereGen;
            cl.UpdateBuffer(c.Movers, 0, spheres);
        }
        VkDefStage st = c.Build!;
        VkColourState s = _vkc;
        int arena0 = s.ArenaUsed;
        Array.Clear(st.Block);
        VkUniformShim.Capture(st.Members, st.Block);
        try
        {
            VkUniformShim.Set(st.L.View, in t.View);
            VkUniformShim.Set(st.L.Grid, (ReadOnlySpan<int>)[t.GridX, t.GridY, t.GridZ]);
            VkUniformShim.Set(st.L.ProjXY, (ReadOnlySpan<float>)[t.ProjX, t.ProjY]);
            VkUniformShim.Set(st.L.Z, (ReadOnlySpan<float>)[t.Near, t.Far]);
            VkUniformShim.Set(st.L.LightCount, t.LightCount);
            VkUniformShim.Set(st.L.Reach, t.Reach);
            VkUniformShim.Set(st.L.LightFloor, (ReadOnlySpan<float>)[t.Floor, t.Gain]);
            VkUniformShim.Set(st.L.BinCap, t.BinCap);
            VkUniformShim.Set(st.L.Atten, (ReadOnlySpan<float>)[t.AttenX, t.AttenY]);
            VkUniformShim.Set(st.L.Hot, (ReadOnlySpan<float>)[t.HotX, t.HotY]);
            VkUniformShim.Set(st.L.ReflK, t.ReflK);
            VkUniformShim.Set(st.L.Rel, (ReadOnlySpan<float>)[t.RelX, t.RelY]);
            VkUniformShim.Set(st.L.Pad, (ReadOnlySpan<float>)[t.PadX, t.PadY]);
            VkUniformShim.Set(st.L.Tally, t.Tally);
            // the dyn-page table the hot partition reads, one float a cube slot -- an ARRAY member, so each element
            // goes at the block's own stride rather than packed against its neighbour
            VkUniformShim.SetEach(st.L.DynPage, CasterPass.CubeDynPage.AsSpan(0, CasterPass.MaxCubes));
        }
        finally { VkUniformShim.EndCapture(); }
        int loose = VkArena(st.Block);
        if (loose < 0) return;
        int len = (s.ArenaUsed - arena0 + 3) & ~3;
        if (len > 0) cl.UpdateBuffer(s.Arena, arena0, s.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));
        cl.SetPipeline(st.Pipe);
        ReadOnlySpan<BufferBinding> ubo = [new(VkGlsl.LooseBinding, s.Arena, loose, st.Block.Length)];
        ReadOnlySpan<BufferBinding> ssbo =
        [
            new(SsboSlots.Lights, VkDeferredLights()), new(SsboSlots.ClusterCount, c.Counts),
            new(SsboSlots.ClusterIdx, c.Idx), new(SsboSlots.Movers, c.Movers), new(SsboSlots.LightStats, c.Stats),
        ];
        cl.BindSet(0, new BindSet(ubo, ssbo, default, default));
        cl.Dispatch((AlloyLights.Clusters + AlloyLights.GroupSize - 1) / AlloyLights.GroupSize);
        c.Fresh = true;
        if (!c.Live)
        {
            c.Live = true;
            log.Always($"AlloyRenderer: the Vulkan froxel build is LIVE -- {t.GridX}x{t.GridY}x{t.GridZ} clusters over "
                     + $"{t.LightCount} lights, the list the deferred record writer picks its shadow candidates from");
        }
    }

    /// <summary>The stage and the three buffers, made once per device. The table is zeroed when new: a reader that
    /// runs before the first build would otherwise take stale memory as a light count in the millions.</summary>
    private static bool VkClusterEnsure(VkBackend vk, CommandList cl, ExtenderLog log)
    {
        VkClusterState c = _vkcl;
        if (c.Build != null) return true;
        c.Build = VkDefBuild(vk, AlloyLights.ShdClusterCs, null, "vk froxel build", log);
        long heads = (long)AlloyLights.Clusters * VkClusterHeads * sizeof(uint);
        long pool = (long)AlloyLights.Clusters * AlloyLights.ClusterCap * sizeof(uint);
        c.Counts = vk.CreateBuffer(new BufferDesc(heads, BufferUse.Storage, MemoryKind.Device, "vk froxel heads"));
        c.Idx = vk.CreateBuffer(new BufferDesc(pool, BufferUse.Storage, MemoryKind.Device, "vk froxel indices"));
        c.Movers = vk.CreateBuffer(new BufferDesc(Math.Max(CasterPass.CubeDynSphere.Length * sizeof(float), 16),
                                                  BufferUse.Storage, MemoryKind.Upload, "vk mover spheres"));
        c.Stats = vk.CreateBuffer(new BufferDesc(16, BufferUse.Storage, MemoryKind.Device, "vk froxel stats"));
        if (c.Build == null || c.Counts.IsNull || c.Idx.IsNull || c.Movers.IsNull || c.Stats.IsNull)
        {
            c.Failed = true;
            log.Always("AlloyRenderer: the Vulkan froxel build did NOT build (logged above) -- the deferred record "
                     + "writer has no light list, so every deferred light draws unshadowed");
            return false;
        }
        cl.ZeroBuffer(c.Counts);
        cl.ZeroBuffer(c.Idx);
        cl.ZeroBuffer(c.Stats);
        return true;
    }
}

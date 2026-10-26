using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The area's IBL probe (<see cref="EnvProbe"/>) on Vulkan: the opaque set and the dome drawn six more times into a
/// small cube from the camera, at the frame's tail, then its box chain, GGX prefilter and SH9 projection by compute --
/// the cube and the SH9 buffer every lit stage reads from then on. Edges and gates are <see cref="EnvProbe"/>'s.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The probe's targets and stages on one device, replaced whole with the device.</summary>
    private sealed class VkProbeState
    {
        public VkBackend? Dev;
        public bool Tried, Failed, Live;
        /// <summary>The capture with its box chain; the prefiltered cube the lit stages sample; the face depth.</summary>
        public ImageId Cap, Tex, Depth;
        /// <summary>The SH9 block, written as storage by the projection and read as <see cref="UboSlots.ProbeSh"/>.</summary>
        public BufferId Sh;
        public VkDefStage? Box, Ggx, Proj;
        public SamplerId Trilinear;
        public int Size, Mips, GLevel = -1, POrigin = -1;
    }

    private static VkProbeState _vkProbe = new();

    /// <summary>The SH9 block's bytes: nine vec4 and the capture origin.</summary>
    private const int VkProbeShBytes = 10 * 16;
    /// <summary>Arena a capture may need: six faces of opaque binds. Less than this left, the capture waits a frame.</summary>
    private const int VkProbeArenaNeed = 1 << 20;

    /// <summary>Do this pass's lit stages read the probe? Not while capturing it (the planar mirror does, as on GL), and not
    /// in the A/B frame, whose GL half has no cube.</summary>
    private static bool VkProbeLive => (!_capturing || _planar) && !VkParity.Active && EnvProbe.Ready && !_vkProbe.Tex.IsNull;

    /// <summary>The probe's mip count as the pass block carries it: 0 = no probe for this pass.</summary>
    private static float VkProbeMips => VkProbeLive ? _vkProbe.Mips : 0f;

    /// <summary>The prefiltered cube for a <c>uProbe</c> declaration, or null when the probe is not live.</summary>
    private static ImageBinding? VkProbeImage(in VkOpaque o)
        => o.Name == "uProbe" && VkProbeLive ? new ImageBinding(o.Binding, _vkProbe.Tex, _vkProbe.Trilinear) : null;

    /// <summary>The SH9 block for the <see cref="UboSlots.ProbeSh"/> point, or null when the probe is not live.</summary>
    private static BufferBinding? VkProbeSh(uint binding)
        => binding == UboSlots.ProbeSh && VkProbeLive ? new BufferBinding(binding, _vkProbe.Sh, 0, VkProbeShBytes) : null;

    /// <summary>A capture, when <see cref="EnvProbe.Wanted"/> asks for one: into <paramref name="cl"/> after the frame's own
    /// passes, which have read the previous cube. The frame's counters and eye come back as they were.</summary>
    private static void VkProbeCapture(VkBackend vk, CommandList cl, VkColourState c, float far, bool lightingOn, ExtenderLog log)
    {
        VkProbeState s = _vkProbe;
        if (s.Dev != vk)
        {
            _vkProbe = s = new VkProbeState { Dev = vk };
            EnvProbe.Revoke();
        }
        if (!lightingOn || VkParity.Active || s.Failed || ArenaCap - c.ArenaUsed < VkProbeArenaNeed) return;
        bool domeArrived = EnvProbe.Ready && !_probeDome && _skyN > 0;
        if (!domeArrived && !EnvProbe.Wanted(_eyePos, _frameSeq)) return;
        if (!ProbeLightSettled() || !EnvProbe.LightsSettled(AlloyLights.LastLightCount)) return;
        if (!VkProbeEnsure(vk, s, log)) return;
        EnvProbe.BeginVk(_eyePos, _frameSeq, far);

        long draws = Counters._draws, batches = Counters._batches;
        Vector3 eye = _eyePos;
        _capturing = true;
        _eyePos = EnvProbe.Origin;
        bool ok = true;
        try
        {
            (float fnear, float ffar) = EnvProbe.FacePlanes;
            int* faceVp = stackalloc int[4] { 0, 0, s.Size, s.Size };
            Vector3 k = ProbeClear();
            // alpha 0 = nothing there, infinitely far: the parallax correction leaves the direction alone
            var clear = new Vector4(k, 0f);
            var r = new VkRange(false, true, false, false, VkTargets.Probe, false);
            CommandList side = c.Side!;
            for (int f = 0; f < 6 && ok; f++)
            {
                Matrix4x4 vp = EnvProbe.FaceVp(f);
                side.Reset();
                int arena0 = c.ArenaUsed;
                var b = new VkBound { Fmt = -1, Tex = -1 };
                VkPassStart(side, s.Size, s.Size);
                ok &= VkIssue(side, ref b, 0, _gOpaqueEnd, r, vp, faceVp, fnear, ffar, false);
                ReadOnlySpan<ColorAttachment> colors = [new(s.Cap, 0, f, LoadOp.Clear, StoreOp.Store, clear)];
                VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(s.Depth, 0, 0, LoadOp.Clear, StoreOp.Store, 1f)));
                // the real dome behind the world, parked at this frame's camera, which is where the probe stands
                ok &= VkRecordSky(vk, cl, c, vp, faceVp, log, s.Cap, f, s.Depth);
            }
            if (ok) ok = VkProbeFilter(cl, s);
        }
        finally
        {
            _capturing = false;
            _eyePos = eye;
            Counters._draws = draws; Counters._batches = batches;
        }
        if (!ok) return;
        EnvProbe.EndVk();
        _probeDome = _skyN > 0;
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan environment probe is LIVE -- {s.Size}px x 6 faces, {s.Mips} mips, GGX "
                     + "prefiltered and SH9 projected; the faces are lit as the Vulkan forward pass lights (no froxel list)");
        }
    }

    /// <summary>The box chain, the GGX levels into the published cube and the SH9 projection, then the cube handed over
    /// to the stages that sample it. False when the arena is full.</summary>
    private static bool VkProbeFilter(CommandList cl, VkProbeState s)
    {
        VkColourState c = _vkc;
        VkDefStage box = s.Box!, ggx = s.Ggx!, proj = s.Proj!;
        int arena0 = c.ArenaUsed;
        Span<int> at = stackalloc int[s.Mips];
        for (int i = 0; i < s.Mips; i++)
        {
            Array.Clear(ggx.Block);
            VkUniformShim.Capture(ggx.Members, ggx.Block);
            VkUniformShim.Set(s.GLevel, i);
            VkUniformShim.EndCapture();
            if ((at[i] = VkArena(ggx.Block)) < 0) return false;
        }
        Array.Clear(proj.Block);
        VkUniformShim.Capture(proj.Members, proj.Block);
        Vector3 o = EnvProbe.Origin;
        VkUniformShim.Set(s.POrigin, [o.X, o.Y, o.Z]);
        VkUniformShim.EndCapture();
        int pAt = VkArena(proj.Block);
        if (pAt < 0) return false;
        int len = (c.ArenaUsed - arena0 + 3) & ~3;
        cl.UpdateBuffer(c.Arena, arena0, c.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));

        cl.SetPipeline(box.Pipe);
        for (int i = 1; i < s.Mips; i++)
        {
            VkProbeBind(cl, s, box, -1, i);
            VkProbeDispatch(cl, s.Size, i);
        }
        cl.SetPipeline(ggx.Pipe);
        for (int i = 0; i < s.Mips; i++)
        {
            VkProbeBind(cl, s, ggx, at[i], i);
            VkProbeDispatch(cl, s.Size, i);
        }
        cl.SetPipeline(proj.Pipe);
        VkProbeBind(cl, s, proj, pAt, 0);
        cl.Dispatch(1);
        ReadOnlySpan<ImageId> read = [s.Tex];
        cl.ReadImages(read);
        return true;
    }

    /// <summary>One level's 8x8 groups over all six faces.</summary>
    private static void VkProbeDispatch(CommandList cl, int size, int mip)
    {
        int n = (Math.Max(size >> mip, 1) + 7) / 8;
        cl.Dispatch(n, n, 6);
    }

    /// <summary>A filter stage's set 0: its loose block at <paramref name="at"/>, the capture sampled whole, the SH9 block as
    /// storage, and level <paramref name="mip"/> stored -- of the capture for the box (the level below as its source), of
    /// the published cube for the prefilter.</summary>
    private static void VkProbeBind(CommandList cl, VkProbeState s, VkDefStage st, int at, int mip)
    {
        VkColourState c = _vkc;
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[2];
        Span<ImageBinding> sampled = stackalloc ImageBinding[2];
        Span<ImageBinding> images = stackalloc ImageBinding[2];
        int nu = 0, ns = 0, nt = 0, ni = 0;
        bool box = st == s.Box;
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock when nu < ubo.Length:
                    ubo[nu++] = o.Binding == UboSlots.Loose && at >= 0 ? new BufferBinding(o.Binding, c.Arena, at, st.Block.Length)
                                                                       : new BufferBinding(o.Binding, c.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock when ns < ssbo.Length:
                    ssbo[ns++] = new BufferBinding(o.Binding, s.Sh, 0, VkProbeShBytes);
                    break;
                case VkBindKind.StorageImage when ni < images.Length:
                    images[ni++] = box ? new ImageBinding(o.Binding, s.Cap, default, o.Binding == 0 ? mip : mip - 1)
                                       : new ImageBinding(o.Binding, s.Tex, default, mip);
                    break;
                case VkBindKind.Sampled when nt < sampled.Length:
                    sampled[nt++] = new ImageBinding(o.Binding, s.Cap, s.Trilinear);
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], images[..ni]));
    }

    /// <summary>The three stages, the two cubes, the face depth and the SH9 block, once per device; a refusal is said
    /// once and the probe sits the session out on Vulkan.</summary>
    private static bool VkProbeEnsure(VkBackend vk, VkProbeState s, ExtenderLog log)
    {
        if (s.Tried) return !s.Failed;
        s.Tried = true;
        s.Size = EnvProbe.Size;
        s.Mips = EnvProbe.MipsOf(s.Size);
        s.Box = VkDefBuild(vk, "cx_probe_box_c", null, "vk probe box chain", log);
        s.Ggx = VkDefBuild(vk, "cx_probe_ggx", null, "vk probe GGX prefilter", log);
        s.Proj = VkDefBuild(vk, "cx_probe_sh", null, "vk probe SH9 projection", log);
        if (s.Ggx != null)
            VkUniformShim.Resolve(s.Ggx.Names, () => s.GLevel = GlProgram.Loc(VkUniformShim.ProgramBase, "uLevel"));
        if (s.Proj != null)
            VkUniformShim.Resolve(s.Proj.Names, () => s.POrigin = GlProgram.Loc(VkUniformShim.ProgramBase, "uOrigin"));
        const ImageUse cube = ImageUse.Sampled | ImageUse.Storage;
        s.Cap = vk.CreateImage(new ImageDesc(ImageKind.Cube, Format.Rgba16F, s.Size, s.Size, 1, s.Mips, 1, 1,
                                             cube | ImageUse.ColorTarget, "vk probe capture"));
        s.Tex = vk.CreateImage(new ImageDesc(ImageKind.Cube, Format.Rgba16F, s.Size, s.Size, 1, s.Mips, 1, 1, cube, "vk probe"));
        s.Depth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.D32F, s.Size, s.Size, 1, 1, 1, 1, ImageUse.DepthTarget,
                                               "vk probe depth"));
        s.Sh = vk.CreateBuffer(new BufferDesc(VkProbeShBytes, BufferUse.Storage | BufferUse.Uniform, MemoryKind.Device, "vk probe sh9"));
        s.Trilinear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Linear, Wrap.Clamp));
        if (s.Box == null || s.Ggx == null || s.Proj == null || s.Cap.IsNull || s.Tex.IsNull || s.Depth.IsNull || s.Sh.IsNull
            || s.Trilinear.IsNull)
        {
            s.Failed = true;
            s.Tex = default;
            log.Always("AlloyRenderer: a Vulkan environment probe stage or target was REFUSED (logged above) -- no IBL probe "
                     + "on Vulkan this session (Alloy parts keep direct lighting only)");
            return false;
        }
        return true;
    }
}

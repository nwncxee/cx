using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// The caster bake on Vulkan: the four depth arrays as images the lit stage samples, the layered depth stages, and
/// one layered pass per target recorded into the shadow bracket's command list -- which the Vulkan colour frame
/// appends ahead of its own, so the maps are written and sampled inside one submit. The view selection, the bake
/// cadence and the queue above it are the pass's own and shared with GL.
/// </summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Which depth array a queued view bakes into. The queue keys on a GL name, which Vulkan has none of;
    /// this is the key both flushes read, resolved from that name at the one place a view is queued.</summary>
    private enum CasterMap : byte { SunStatic, SunDyn, Cubes, CubeDyn, Other }

    /// <summary>One translated depth program: its modules, the pipelines it has made by state key, and the offsets
    /// of the three loose members a masked stage reads out of its own block.</summary>
    private sealed class VkCastProg
    {
        public ShaderId Vs, Fs;
        public string Label = "";
        public VkOpaque[] Opaques = [];
        /// <summary>The loose block's size, and the byte offset of each member in it; -1 for one it does not declare.</summary>
        public int Bytes;
        public int AtDither = -1, AtEnv = -1, AtMip = -1;
        public readonly Dictionary<int, PipelineId> Pipes = new();
    }

    /// <summary>Everything the Vulkan device owns for the bake, replaced whole when the device is.</summary>
    private sealed class CasterVkState
    {
        public VkBackend? Dev;
        /// <summary>A program or target was refused: the bake stands down for the session, loudly.</summary>
        public bool Failed, Live, ContentSaid, OverflowSaid, NoTargetSaid;
        /// <summary>The bracket's uploads, appended by the colour frame ahead of its own passes.</summary>
        public CommandList? Cmds;
        /// <summary>Views the bracket queued for the colour frame to flush; 0 = nothing to bake.</summary>
        public int Queued;
        public ImageId Cascades, SunDyn, Cubes, CubeDyn;
        /// <summary>The size and layer count each array was made at: a knob change remakes it.</summary>
        public int SunSize, SunLayers, DynSize, DynLayers, CubeSize, CubePages, CubeDynSize, CubeDynPages;
        public VkCastProg? Plain, Mask, MaskH;
        /// <summary>The three cut kinds' loose blocks, one after another at the device's alignment.</summary>
        public BufferId Cuts;
        public int CutStride;
        public readonly VertexLayoutId[] Layouts = new VertexLayoutId[MeshArena.Formats];
        /// <summary>Views this frame's flush rendered, and chunks it took.</summary>
        public int Baked, Chunks;
    }

    private static CasterVkState _vk = new();

    /// <summary>Cut kinds a masked stage may be bound at, as <see cref="CasterCut"/> counts them.</summary>
    private const int CutKinds = 3;

    /// <summary>Is the Vulkan bake armed? Nothing stands it down but a refusal, so the layered path is the only one.</summary>
    private static bool VkArmed => !_vk.Failed && _vk.Plain != null;

    /// <summary>The bracket's uploads for the colour frame to append, or null when it recorded none.</summary>
    internal static CommandList? VkBracket => _vk.Cmds is { Length: > 0 } c ? c : null;

    /// <summary>The queued views into <paramref name="cl"/>, called by the colour frame once its uploads are
    /// recorded: the bake binds the record, bone and handle buffers that <c>VkUploads</c> just published, and a
    /// buffer it grew would leave a bind recorded in the bracket naming a dead id. False (the device refused,
    /// logged) stands the published maps down -- the frame's own publish is already out, so the blanket re-bakes.</summary>
    internal static bool VkFlush(CommandList cl)
    {
        int n = _vk.Queued;
        _vk.Queued = 0;
        if (!VkArmed) return true;
        // the hand-over runs whatever the queue held: a frame that only CLEARED a layer (the static faces of a
        // roaming light) still left that array in its write layout
        bool ok = n == 0 || VkFlushLayered(cl, n);
        VkHandOver(cl);
        if (ok) return true;
        _layerFlushFails++;
        CascadeCount = 0; CubeCount = 0;
        Array.Clear(SunDynMask); Array.Clear(CubeDynPage);
        _invalidate = true; _sunScanned = false; _cubeScanned = false;
        return false;
    }

    /// <summary>The sun cascade array the lit stage samples: cascades plus the cover layer, hardware-PCF filtered.
    /// Null until the first bake makes it. The deferred passes bind this where GL binds <c>CascadeTex</c>.</summary>
    internal static ImageId VkCascadeMap => _vk.Cascades;

    /// <summary>The sun's moving-caster mirror, where <c>/shadow split</c> keeps one.</summary>
    internal static ImageId VkSunDynMap => _vk.SunDyn;

    /// <summary>The point lights' static shadow cubes, a cube array of <see cref="MaxCubes"/> pages.</summary>
    internal static ImageId VkCubeMap => _vk.Cubes;

    /// <summary>...and the moving-caster cube pool, where one is configured.</summary>
    internal static ImageId VkCubeDynMap => _vk.CubeDyn;

    /// <summary>The frame's bake: open the bracket's recording. Called at the head of the shadow bracket, so a frame
    /// that bakes nothing appends nothing. False (logged) when the device refused the bake.</summary>
    private static bool VkBegin(ExtenderLog log)
    {
        if (AlloyDevice.Vulkan(log) is not VkBackend vk) return false;
        if (_vk.Dev != vk) _vk = new CasterVkState { Dev = vk };
        var s = _vk;
        if (s.Failed) return false;
        if (s.Plain == null && !VkMakePrograms(vk, log)) { s.Failed = true; return false; }
        s.Cmds ??= new CommandList("vulkan caster bake", 1 << 20, log);
        s.Cmds.Reset();
        s.Baked = s.Chunks = 0;
        if (!CasterGatherVk.Ensure(vk, log)) { s.Failed = true; return false; }
        // the bounds FIRST: the colour build publishes them after this bracket, so these are the previous frame's
        // -- which is what the GL bake reads too -- and sizing them here keeps every dispatch below naming a live id
        AlloyRenderer.VkRecordCasterBounds(vk, s.Cmds);
        return true;
    }

    /// <summary>Clear one layer of a Vulkan depth array without a bake: the static faces of a roaming light. An
    /// uncleared layer is not "empty", it is last frame's depth.</summary>
    private static bool VkClearLayer(CasterMap map, int size, int layer)
    {
        var s = _vk;
        if (!VkArmed || s.Cmds == null) return false;
        ImageId img = VkMapOf(map);
        if (img.IsNull) return false;
        s.Cmds.BeginRenderPass(new RenderPassDesc(default, new DepthAttachment(img, 0, layer, LoadOp.Clear, StoreOp.Store, 1f)));
        s.Cmds.EndRenderPass();
        _ = size;
        return true;
    }

    /// <summary>Stage the frame's caster source for the Vulkan gather, as <see cref="CasterGather.Begin"/> does for
    /// GL's. Called from the one staging walk, so both backends select out of the same records.</summary>
    private static bool VkGatherBegin(int drawN, CasterSrcRec* src)
        => _vk.Cmds != null && _vk.Dev is VkBackend vk && CasterGatherVk.Begin(vk, _vk.Cmds, drawN, _gGroups, src, true);

    /// <summary>The frame's dynamic source on whichever backend bakes it.</summary>
    private static bool VkDynamicSource(int m, CasterSrcRec* dst)
        => Renderer.OnVulkan
         ? _vk.Cmds != null && _vk.Dev is VkBackend vk && CasterGatherVk.DynamicSource(vk, _vk.Cmds, m, dst)
         : CasterGather.DynamicSource(m, dst);

    /// <summary>No dynamic source this frame, on whichever backend bakes it.</summary>
    private static void StandDynamicSource()
    {
        if (Renderer.OnVulkan) CasterGatherVk.StandDynamic();
        else CasterGather.StandDynamic();
    }

    /// <summary>A view whose target the Vulkan bake has no array for: refused, so its caller publishes nothing.
    /// Said once per target, because the caller retries it every frame.</summary>
    private static bool VkNoTarget()
    {
        if (!_vk.NoTargetSaid)
        {
            _vk.NoTargetSaid = true;
            _log?.Info("CasterPass: a queued view's target is not on Vulkan yet (the overhead sky view, a pair tile) "
                     + "-- that view is refused and its map stands down");
        }
        return false;
    }

    /// <summary>The frame queued more views than the queue holds. GL falls back to the per-view path; Vulkan has
    /// none, so the view is refused and the frame publishes nothing -- said once.</summary>
    private static bool VkQueueFull()
    {
        if (!_vk.OverflowSaid)
        {
            _vk.OverflowSaid = true;
            _log?.Always($"CasterPass: more than {MaxBakeViews} views queued in one Vulkan frame -- the rest are "
                       + "refused and nothing is published for the frame; the blanket re-bakes it");
        }
        return false;
    }

    /// <summary>The three plain depth programs, the vertex layouts and the cut blocks, once per device.</summary>
    private static bool VkMakePrograms(VkBackend vk, ExtenderLog log)
    {
        var s = _vk;
        for (int f = 0; f < MeshArena.Formats; f++)
        {
            s.Layouts[f] = vk.CreateVertexLayout(MeshArena.VkLayout((MeshArena.Fmt)f));
            if (s.Layouts[f].IsNull) { log.Always($"CasterPass: the Vulkan device REFUSED arena layout {(MeshArena.Fmt)f}"); return false; }
        }
        s.Plain = VkCastBuild(vk, CastLayerVs, CastFs, "caster layered depth", log);
        s.Mask = VkCastBuild(vk, CastLayerMaskVs, CastMaskFs, "caster layered masked depth", log);
        s.MaskH = VkCastBuild(vk, CastLayerMaskHVs, CastMaskHFs, "caster layered handle-masked depth", log);
        s.CutStride = Math.Max(Math.Max(s.Plain?.Bytes ?? 0, s.Mask?.Bytes ?? 0), 16);
        s.CutStride = (s.CutStride + Math.Max(vk.UniformAlign, 16) - 1) & ~(Math.Max(vk.UniformAlign, 16) - 1);
        s.Cuts = vk.CreateBuffer(new BufferDesc((long)s.CutStride * CutKinds, BufferUse.Uniform, MemoryKind.Upload, "caster cut blocks"));
        if (s.Plain == null || s.Mask == null || s.MaskH == null || s.Cuts.IsNull)
        {
            log.Always("CasterPass: a Vulkan caster depth program or its cut block was REFUSED (logged above) -- no Alloy "
                     + "shadow is baked this session");
            return false;
        }
        log.Info("CasterPass: the Vulkan caster depth programs are built -- the solid stage, the bound-texture mask and "
               + "the handle mask, each layered off the view table");
        return true;
    }

    /// <summary>One depth program: translated, compiled, and its loose block's three members located in the module's
    /// own offsets. Null (logged) when it did not translate or compile.</summary>
    private static VkCastProg? VkCastBuild(VkBackend vk, string vs, string fs, string label, ExtenderLog log)
    {
        var units = new Dictionary<string, uint> { ["uTex"] = 0 };
        VkProgramText t = VkGlsl.Translate(vs, fs, units, glDepth: false);
        if (t.Error != null) { log.Always($"CasterPass: the Vulkan {label} program did not translate -- {t.Error}"); return null; }
        byte[]? vb = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"vk {label} vs", ShaderKind.VertexShader, out string ve);
        byte[]? fb = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"vk {label} fs", ShaderKind.FragmentShader, out string fe);
        if (vb == null || fb == null)
        {
            log.Always($"CasterPass: the Vulkan {label} program did not compile -- {(vb == null ? ve : fe)}");
            return null;
        }
        var p = new VkCastProg { Label = label, Opaques = t.Opaques.ToArray() };
        p.Vs = vk.CreateShader(ShaderStage.Vertex, vb, $"vk {label} vs");
        p.Fs = vk.CreateShader(ShaderStage.Fragment, fb, $"vk {label} fs");
        if (p.Vs.IsNull || p.Fs.IsNull) return null;
        var names = new List<string>(t.Loose.Count);
        foreach (VkLoose u in t.Loose) names.Add(u.Name);
        uint at = VulkanToolchain.UniformBase + VkGlsl.LooseBinding;
        if (names.Count > 0 && (VkSpirv.Block(fb, 0, at, names) ?? VkSpirv.Block(vb, 0, at, names)) is { } l)
        {
            p.Bytes = l.Bytes;
            p.AtDither = l.Members.TryGetValue("uDither", out var d) ? d.Offset : -1;
            p.AtEnv = l.Members.TryGetValue("uEnvAlpha", out var e) ? e.Offset : -1;
            p.AtMip = l.Members.TryGetValue("uAlphaMip", out var m) ? m.Offset : -1;
        }
        return p;
    }

    /// <summary>The three cut kinds' loose blocks for this frame -- the dither block and the env-alpha flag each kind
    /// asks for, over this frame's mip compensation -- into the device's copy, once per flush.</summary>
    private static void VkCutBlocks(CommandList cl)
    {
        var s = _vk;
        int stride = s.CutStride;
        Span<byte> all = stackalloc byte[stride * CutKinds];
        all.Clear();
        for (int k = 0; k < CutKinds; k++)
        {
            var kind = (CasterCut)k;
            Span<byte> one = all.Slice(k * stride, stride);
            // the three loose members a masked stage reads; the solid stage declares none and reads this block never
            if (s.Mask!.AtDither >= 0) MemoryMarshal.Write(one[s.Mask.AtDither..], DitherFor(kind));
            if (s.Mask.AtEnv >= 0) MemoryMarshal.Write(one[s.Mask.AtEnv..], kind == CasterCut.EnvAlpha ? 1 : 0);
            if (s.Mask.AtMip >= 0) MemoryMarshal.Write(one[s.Mask.AtMip..], Renderer.CutMip.V);
        }
        cl.UpdateBuffer(s.Cuts, 0, all);
    }

    /// <summary>The queued views on Vulkan, one layered pass per distinct target. False = nothing usable was
    /// recorded, so the caller publishes nothing this frame.</summary>
    private static bool VkFlushLayered(CommandList cl, int n)
    {
        var s = _vk;
        if (!VkArmed || _vk.Dev is not VkBackend vk) return false;
        VkCutBlocks(cl);
        // one chunk ceiling for the frame, so every chunk's dispatch sizes the layered buffers alike
        int chunk = Math.Min(Math.Max(1, MaxLayerCmds / Math.Max(_gDrawN, 1)), MaxBakeViews);
        Span<CasterMap> seen = stackalloc CasterMap[8];
        int nt = 0;
        for (int k = 0; k < n; k++)
        {
            bool dup = false;
            for (int t = 0; t < nt && !dup; t++) dup = seen[t] == _qMap[k];
            if (dup) continue;
            if (nt >= seen.Length) return false;
            seen[nt++] = _qMap[k];
        }
        for (int t = 0; t < nt; t++)
            if (!VkFlushTarget(vk, cl, seen[t], n, chunk)) return false;
        if (!s.Live)
        {
            s.Live = true;
            _log?.Always($"CasterPass: the Vulkan caster bake is LIVE -- {s.Baked} views over {nt} depth arrays in "
                       + $"{s.Chunks} layered passes, selected by the gather on Vulkan");
        }
        return true;
    }

    /// <summary>Hand every depth array over for sampling, outside every pass: the colour bracket's first bind of
    /// one may land after a draw of a program that does not declare it, where no barrier can be placed.</summary>
    private static void VkHandOver(CommandList cl)
    {
        var s = _vk;
        ReadOnlySpan<ImageId> maps = [s.Cascades, s.SunDyn, s.Cubes, s.CubeDyn];
        cl.ReadImages(maps);
    }

    /// <summary>One target's queued views: every stale layer cleared on its own, then one layered pass per chunk over
    /// the whole array, the stages routing by <c>gl_Layer</c> out of the chunk's view table.</summary>
    private static bool VkFlushTarget(VkBackend vk, CommandList cl, CasterMap map, int n, int chunk)
    {
        ImageId img = VkMapOf(map);
        if (img.IsNull) return false;
        int size = 0;
        bool clamp = map is CasterMap.SunStatic or CasterMap.SunDyn;
        for (int k = 0; k < n; k++)
        {
            if (_qMap[k] != map) continue;
            size = _qSize[k];
            // one pass per stale layer, cleared and nothing drawn: a layered attachment clears every layer at once,
            // fresh ones included, so the per-layer loop here is correctness rather than thrift
            cl.BeginRenderPass(new RenderPassDesc(default, new DepthAttachment(img, 0, _qLayer[k], LoadOp.Clear, StoreOp.Store, 1f)));
            cl.EndRenderPass();
            _vk.Baked++;
        }
        if (size <= 0) return true;
        int k2 = 0;
        while (true)
        {
            int vn = 0, sets = 0;
            fixed (CasterViewRec* vs = _viewStage)
                while (k2 < n && vn < chunk)
                { if (_qMap[k2] == map) { sets |= 1 << _qSet[k2]; StageView(vs, vn++, k2); } k2++; }
            if (vn == 0) break;
            bool ok;
            fixed (CasterViewRec* vs = _viewStage)
                ok = CasterGatherVk.DispatchLayer(vk, cl, vs, vn, sets == 1 << CasterGather.SetDynamic, chunk);
            if (!ok) return false;
            cl.BeginRenderPass(new RenderPassDesc(default, new DepthAttachment(img, 0, -1, LoadOp.Load, StoreOp.Store, 1f)));
            cl.SetViewport(0, 0, size, size);
            if (!MeshArena.VkEbo.IsNull) cl.BindIndexBuffer(MeshArena.VkEbo, IndexType.U16, 0);
            VkDrawLayered(cl, vn, sets, clamp);
            cl.EndRenderPass();
            _vk.Chunks++;
        }
        return true;
    }

    /// <summary>The layered pass's batch loop, <see cref="DrawLayered"/>'s walk: the program, its cut block and the
    /// batch's mask bound only where they changed, then one indirect-count draw per batch for all the chunk's views.</summary>
    private static void VkDrawLayered(CommandList cl, int vn, int sets, bool clamp)
    {
        var s = _vk;
        int boundFmt = -1, boundMode = -1, boundTex = -1, boundCut = -1;
        PipelineId pipe = default;
        for (int g = 0; g < _gGroups; g++)
        {
            if (BatchIdle(g, sets)) { _mdiSkipped++; continue; }
            int fmt = _gVao[g] - AlloyRenderer.DbVaoBase;
            if ((uint)fmt >= MeshArena.Formats || MeshArena.VkVbo((MeshArena.Fmt)fmt).IsNull) { _mdiSkipped++; continue; }
            int mode = VkStageOf(g);
            VkCastProg p = mode == 1 ? s.Mask! : mode == 2 ? s.MaskH! : s.Plain!;
            PipelineId want = VkCastPipeline(p, fmt, clamp);
            if (want.IsNull) { _mdiSkipped++; continue; }
            int tex = mode == 1 ? _gTex[g] : 0;
            int cut = mode == 0 ? 0 : (int)_gCut[g];
            if (mode != boundMode || cut != boundCut || tex != boundTex)
            {
                VkCastBind(cl, p, cut, tex);
                boundMode = mode; boundCut = cut; boundTex = tex;
            }
            if (want != pipe) { cl.SetPipeline(want); pipe = want; }
            if (fmt != boundFmt) { cl.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)fmt)); boundFmt = fmt; }
            float alphaCut = mode == 0 ? -1f : Threshold((CasterCut)cut);
            cl.PushConstants(CutPushAt, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(in alphaCut)));
            cl.DrawIndexedIndirect(CasterGatherVk.LayerCommands, (long)_gStart[g] * vn * CasterGather.CmdBytes,
                                   _gLen[g] * vn, CasterGatherVk.LayerCounts, (long)g * sizeof(uint));
            _mdiCalls++;
        }
    }

    /// <summary>Where the cut sits in the translated push block: every member of <c>VkGlsl.Push</c> is four bytes,
    /// so taking the offset from that table rather than restating it is what keeps the two from drifting. The cut is
    /// the only member a caster depth stage names; the rest stay at the zero no draw here writes.</summary>
    private static int CutPushAt => Array.FindIndex(VkGlsl.Push, x => x.Name == "uAlphaCut") * sizeof(float);

    /// <summary>Batch <paramref name="g"/>'s stage on Vulkan: <see cref="StageOf"/>'s, with a content pair's twin
    /// falling back to the plain stage of its kind -- the twins are not translated yet, and the fallback casts the
    /// part unmoved rather than not at all, which is what GL does when a pair's twin did not build.</summary>
    private static int VkStageOf(int g)
    {
        if (_gContent[g] == 0) return StageOf(g);
        if (!_vk.ContentSaid)
        {
            _vk.ContentSaid = true;
            _log?.Always("CasterPass: a content pair's caster twin is NOT RUN on Vulkan -- its parts cast through the "
                       + "plain stage, so a stage that moves its vertices casts them unmoved");
        }
        return _gH[g] ? 2 : _gTex[g] != 0 ? 1 : 0;
    }

    /// <summary>One program's set 0: the records, bones and handles the vertex stage reads, the chunk's view table,
    /// the cut kind's loose block and the batch's bound mask, each where the program declares it.</summary>
    private static void VkCastBind(CommandList cl, VkCastProg p, int cut, int tex)
    {
        var s = _vk;
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[8];
        Span<ImageBinding> sampled = stackalloc ImageBinding[4];
        int nu = 0, ns = 0, nt = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    ubo[nu++] = o.Binding == UboSlots.Loose && p.Bytes > 0
                              ? new BufferBinding(o.Binding, s.Cuts, (long)cut * s.CutStride, p.Bytes)
                              : new BufferBinding(o.Binding, AlloyRenderer.VkZeroUbo);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding switch
                    {
                        SsboSlots.Draw => AlloyRenderer.VkDrawRecords,
                        SsboSlots.Bones => AlloyRenderer.VkBones,
                        SsboSlots.TexH => AlloyRenderer.VkTexH,
                        SsboSlots.ViewTbl => CasterGatherVk.Views,
                        _ => AlloyRenderer.VkZeroSsbo,
                    });
                    break;
                default:
                    ImageId img = o.Name == "uTex" && tex != 0 ? VkTextures.Image(tex) : default;
                    sampled[nt++] = img.IsNull ? new ImageBinding(o.Binding, AlloyRenderer.VkStandIn2D)
                                               : new ImageBinding(o.Binding, img, VkTextures.SamplerId(tex));
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
    }

    /// <summary>Program <paramref name="p"/>'s depth-only pipeline for arena format <paramref name="fmt"/>, with the
    /// clamp a directional view needs, made on first ask. Null id when the device refused it.</summary>
    private static PipelineId VkCastPipeline(VkCastProg p, int fmt, bool clamp)
    {
        int key = fmt | (clamp ? 1 << 8 : 0);
        if (p.Pipes.TryGetValue(key, out PipelineId id)) return id;
        var s = _vk;
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Layout = s.Layouts[fmt], Topo = Topology.Triangles,
            // #292: cull OFF, deliberately unlike the colour pass -- which face is the front one is the LIGHT's
            // question here, and culling would let the light through a single-sided wall it stands behind
            Raster = new RasterState(CullMode.None, DepthClamp: clamp),
            Depth = new DepthState(true, true, CompareOp.LessEqual),
            Label = $"vk {p.Label} {(MeshArena.Fmt)fmt}{(clamp ? " clamped" : "")}",
        };
        d.Targets.Depth = Format.D32F;
        d.Targets.Samples = 1;
        id = s.Dev!.CreatePipeline(in d);
        p.Pipes[key] = id;
        return id;
    }

    /// <summary>The image behind a queued view's target key, made at the size and layer count the knobs ask for.</summary>
    private static ImageId VkMapOf(CasterMap map)
    {
        var s = _vk;
        return map switch
        {
            // #617: the cover map rides the static array's last layer, as it does on GL
            CasterMap.SunStatic => VkArray(ref s.Cascades, ref s.SunSize, ref s.SunLayers, ImageKind.Tex2DArray,
                                           _mapSize, _cascades + CoverLayers, "vk sun cascades"),
            CasterMap.SunDyn => VkArray(ref s.SunDyn, ref s.DynSize, ref s.DynLayers, ImageKind.Tex2DArray,
                                        _mapSize, _cascades, "vk sun movers"),
            CasterMap.Cubes => VkArray(ref s.Cubes, ref s.CubeSize, ref s.CubePages, ImageKind.CubeArray,
                                       _cubeSize, _cubeLights, "vk light cubes"),
            CasterMap.CubeDyn => VkArray(ref s.CubeDyn, ref s.CubeDynSize, ref s.CubeDynPages, ImageKind.CubeArray,
                                         _cubeDynSize, _cubeDynPages, "vk mover cubes"),
            _ => default,
        };
    }

    /// <summary>One depth array, remade when the size or layer count moved. Zero layers is no array at all -- the
    /// mover pool that <c>/shadow dyncount 0</c> switches off.</summary>
    private static ImageId VkArray(ref ImageId id, ref int madeSize, ref int madeLayers, ImageKind kind, int size,
                                   int layers, string label)
    {
        if (layers <= 0 || size <= 0) return default;
        if (!id.IsNull && madeSize == size && madeLayers == layers) return id;
        var s = _vk;
        s.Dev!.Destroy(id);
        id = s.Dev.CreateImage(new ImageDesc(kind, Format.D32F, size, size, 1, 1, layers, 1,
                                             ImageUse.DepthTarget | ImageUse.Sampled, label));
        if (id.IsNull)
        {
            s.Failed = true;
            _log?.Always($"CasterPass: the Vulkan device REFUSED a {size}x{size} {kind} of {layers} layers for {label} "
                       + "-- no Alloy shadow is baked this session");
            return default;
        }
        madeSize = size; madeLayers = layers;
        return id;
    }
}

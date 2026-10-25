using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>What a recorded pass has bound since its render pass began: a change re-records only what changed.</summary>
    private struct VkBound
    {
        public VkProg? Prog;
        public PipelineId Pipe;
        public int Fmt, Tex;
        public DepthState Depth;
        public RasterState Raster;
        public bool StateSet;
    }

    /// <summary>How one range of batches draws: the depth rule it inherits, whether it is the wash interiors' pass, the
    /// split washes' interiors having run, and the attachment set.</summary>
    private readonly record struct VkRange(bool MatchDepth, bool DepthMask, bool Interior, bool SplitRan, VkTargets Targets, bool Gbuf);

    /// <summary>The colour bracket into <paramref name="cl"/> after the cull: uploads, the depth prepass, the opaque,
    /// cutout and wash-interior pass into the scene colour and G-buffer, the blended and additive pass, then the scene
    /// colour encoded into <paramref name="composite"/>. False (logged) when the device refused something.</summary>
    internal static bool VkRecordFrame(VkBackend vk, CommandList cl, ImageId composite, in Matrix4x4 vp, int* viewport,
                                       int* display, float near, float far, bool lightingOn, bool perspective, int groups,
                                       ExtenderLog log)
    {
        int w = viewport[2], h = viewport[3];
        if (!VkEnsure(vk, w, h, log)) return false;
        VkColourState s = _vkc;
        s.ArenaUsed = 0;
        s.Drawn = s.Skipped = 0;
        if (!VkUploads(vk, cl)) return false;
        bool indirect = CullPassVk.Ran;
        int viz = _capturing ? 0 : _lightViz;
        bool gbuf = lightingOn && perspective && Lighting.Deferred.V && (viz == 0 || viz == VizLightFrom);
        VkTargets colour = gbuf ? VkTargets.Colour : VkTargets.Forward;

        if (!VkPrepass(cl, s, groups, vp, viewport, near, far, indirect)) return false;

        CommandList side = s.Side!;
        side.Reset();
        int arena0 = s.ArenaUsed;
        var b = new VkBound { Fmt = -1, Tex = -1 };
        VkPassStart(side, w, h);
        bool ok = true;
        var plain = new VkRange(_depthEqual, true, false, false, colour, gbuf);
        ok &= VkIssueClass(side, ref b, 0, _gCutoutStart, plain, vp, viewport, near, far, indirect);
        ok &= VkIssueClass(side, ref b, _gCutoutStart, _gA2cStart, plain, vp, viewport, near, far, indirect);
        ok &= VkIssue(side, ref b, _gA2cStart, _gOpaqueEnd, plain with { MatchDepth = false }, vp, viewport, near, far, indirect);
        bool washes = _gOpaqueEnd < _gBlendEnd;
        if (washes) ok &= VkIssue(side, ref b, _gOpaqueEnd, _gBlendEnd, plain with { MatchDepth = false, Interior = true }, vp, viewport, near, far, indirect);
        if (!ok) return false;
        Span<ColorAttachment> colors = stackalloc ColorAttachment[ColourTargets];
        Vector3 c = PostChain.LastClear;
        var clear = new Vector4(MathF.Pow(MathF.Max(c.X, 0f), AlloyLights.SrgbGamma), MathF.Pow(MathF.Max(c.Y, 0f), AlloyLights.SrgbGamma),
                                MathF.Pow(MathF.Max(c.Z, 0f), AlloyLights.SrgbGamma), 1f);
        colors[0] = new ColorAttachment(s.Hdr, 0, 0, LoadOp.Clear, StoreOp.Store, clear);
        int n = 1;
        if (gbuf)
        {
            n = ColourTargets;
            colors[GbufFirst] = new ColorAttachment(s.G0, 0, 0, LoadOp.Clear, StoreOp.Store, default);
            colors[GbufFirst + 1] = new ColorAttachment(s.G1, 0, 0, LoadOp.DontCare, StoreOp.Store, default);
            colors[GbufFirst + 2] = new ColorAttachment(s.G2, 0, 0, LoadOp.DontCare, StoreOp.Store, default);
        }
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors[..n], new DepthAttachment(s.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));

        side.Reset();
        arena0 = s.ArenaUsed;
        b = new VkBound { Fmt = -1, Tex = -1 };
        VkPassStart(side, w, h);
        var fwd = new VkRange(false, true, false, washes, VkTargets.Forward, false);
        ok &= VkIssue(side, ref b, _gOpaqueEnd, _gBlendEnd, fwd, vp, viewport, near, far, indirect);
        ok &= VkIssue(side, ref b, _gWaterEnd, groups, fwd with { DepthMask = false }, vp, viewport, near, far, indirect);
        if (!ok) return false;
        colors[0] = new ColorAttachment(s.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default);
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors[..1], new DepthAttachment(s.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));

        VkEncode(cl, s, composite, display);
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan colour bracket is LIVE -- {s.Drawn} draws, {s.Skipped} skipped "
                     + "(water, and any batch whose mesh or pipeline is not on Vulkan yet); lit by the sun and ambient only "
                     + "until shadows, point lights, the probe and SSAO port");
        }
        return true;
    }

    /// <summary>Viewport, index buffer and depth-bias constants for a pass recorded aside.</summary>
    private static void VkPassStart(CommandList side, int w, int h)
    {
        side.SetViewport(0, 0, w, h);
        if (!MeshArena.VkEbo.IsNull) side.BindIndexBuffer(MeshArena.VkEbo, IndexType.U16, 0);
        side.SetDepthBias(Issue.WashOffsetFactor, Issue.WashOffsetUnits);
    }

    /// <summary>The arena bytes the pass's binds gathered, uploaded ahead of it, then the pass and its draws.</summary>
    private static void VkFlushPass(CommandList cl, CommandList side, int arena0, in RenderPassDesc rp)
    {
        VkColourState s = _vkc;
        int len = (s.ArenaUsed - arena0 + 3) & ~3;
        if (len > 0) cl.UpdateBuffer(s.Arena, arena0, s.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));
        cl.BeginRenderPass(in rp);
        cl.Append(side);
        cl.EndRenderPass();
    }

    /// <summary>The records, draw order, bones and handle table this frame's draws read, recorded ahead of the passes.</summary>
    private static bool VkUploads(VkBackend vk, CommandList cl)
    {
        VkColourState s = _vkc;
        int cap = _recOwnCap + MaxSkyDraws;
        bool whole = s.DirtyAll;
        if (s.RecCap < cap)
        {
            vk.Destroy(s.Records); vk.Destroy(s.TexH);
            s.Records = vk.CreateBuffer(new BufferDesc((long)cap * DrawFloats * sizeof(float), BufferUse.Storage, MemoryKind.Upload, "vk draw records"));
            s.TexH = vk.CreateBuffer(new BufferDesc((long)cap * TexHandles.MaterialSlots * 8, BufferUse.Storage, MemoryKind.Upload, "vk handle table"));
            s.TexHTable = new uint[cap * TexHandles.MaterialSlots * 2];
            s.RecCap = cap;
            whole = true;
            if (s.Records.IsNull || s.TexH.IsNull) { s.RecCap = 0; return false; }
        }
        s.WhiteIndex = VkTextures.Index(OwnedTextures.WhiteTid());
        s.NullIndex = VkTextures.Index(OwnedTextures.NullTid());
        ReadOnlySpan<byte> rec = MemoryMarshal.AsBytes(_dData.AsSpan(0, cap));
        int rb = DrawFloats * sizeof(float), hb = TexHandles.MaterialSlots * 8;
        if (whole)
        {
            for (int r = 0; r < cap; r++) VkHandles(r);
            cl.UpdateBuffer(s.Records, 0, rec);
            cl.UpdateBuffer(s.TexH, 0, MemoryMarshal.AsBytes(s.TexHTable.AsSpan()));
        }
        else
        {
            int nd = Math.Min(s.DirtyN, s.Dirty.Length);
            Array.Sort(s.Dirty, 0, nd);
            for (int d = 0; d < nd;)
            {
                int first = s.Dirty[d], last = first;
                while (++d < nd && s.Dirty[d] <= last + 1) last = s.Dirty[d];
                if (first >= cap) break;
                last = Math.Min(last, cap - 1);
                for (int r = first; r <= last; r++) VkHandles(r);
                cl.UpdateBuffer(s.Records, (long)first * rb, rec.Slice(first * rb, (last - first + 1) * rb));
                cl.UpdateBuffer(s.TexH, (long)first * hb, MemoryMarshal.AsBytes(s.TexHTable.AsSpan(first * TexHandles.MaterialSlots * 2, (last - first + 1) * TexHandles.MaterialSlots * 2)));
            }
        }
        s.DirtyN = 0; s.DirtyAll = false;
        long slotBytes = Math.Max((long)_drawN * sizeof(int), 4);
        if (!VkGrow(vk, ref s.Slots, ref s.SlotCap, slotBytes, "vk draw order")) return false;
        if (_drawN > 0) cl.UpdateBuffer(s.Slots, 0, MemoryMarshal.AsBytes(_dSlot.AsSpan(0, _drawN)));
        if (Db.PltColors.Sends != s.PltSends && !Db.PltColors.Sent.IsEmpty)
        {
            ReadOnlySpan<byte> rows = MemoryMarshal.AsBytes(Db.PltColors.Sent);
            if (!VkGrow(vk, ref s.Plt, ref s.PltCap, rows.Length, "vk plt rows")) return false;
            cl.ZeroBuffer(s.Plt);
            cl.UpdateBuffer(s.Plt, 0, rows);
            s.PltSends = Db.PltColors.Sends;
        }
        if (Db.PltPalettes.Sends != s.PaletteSends && !Db.PltPalettes.Sent.IsEmpty)
        {
            vk.Destroy(s.Palette);
            s.Palette = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, Db.PltPalettes.Width, Db.PltPalettes.Height,
                                                     1, 1, 1, 1, ImageUse.Sampled | ImageUse.CopyDst, "vk plt palette"));
            if (!s.Palette.IsNull) vk.Upload(s.Palette, 0, 0, Db.PltPalettes.Sent);
            s.PaletteSends = Db.PltPalettes.Sends;
        }
        long boneBytes = _skinning ? (long)_boneSlots * BoneFloats * sizeof(float) : 0;
        if (!VkGrow(vk, ref s.Bones, ref s.BoneCap, Math.Max(boneBytes, 64), "vk bones")) return false;
        if (boneBytes > 0) cl.UpdateBuffer(s.Bones, 0, new ReadOnlySpan<byte>(_boneStage, (int)boneBytes));
        return true;
    }

    /// <summary>Record <paramref name="r"/>'s handles as Vulkan descriptor indices, no index left zero: the diffuse
    /// falls to the white stand-in a record means by none, every map slot to the transparent-black one.</summary>
    private static void VkHandles(int r)
    {
        VkColourState s = _vkc;
        int at = r * TexHandles.MaterialSlots * 2;
        for (int k = 0; k < TexHandles.MaterialSlots; k++)
        {
            ulong v = _dTexH[r][k];
            uint i = v != 0 ? VkTextures.IndexOfHandle(v) : 0;
            s.TexHTable[at + 2 * k] = i != 0 ? i : k == 0 ? s.WhiteIndex : s.NullIndex;
            s.TexHTable[at + 2 * k + 1] = 0;
        }
    }

    private static bool VkGrow(VkBackend vk, ref BufferId id, ref long cap, long bytes, string label)
    {
        if (!id.IsNull && bytes <= cap) return true;
        vk.Destroy(id);
        cap = Math.Max(bytes, cap * 2);
        id = vk.CreateBuffer(new BufferDesc(cap, BufferUse.Storage, MemoryKind.Upload, label));
        if (id.IsNull) cap = 0;
        return !id.IsNull;
    }

    /// <summary>The depth prepass as <see cref="DepthPrepassPass"/> walks it -- plain, keyhole, then the feature sweep --
    /// with <see cref="_gPrepassed"/> measured as it draws, into a depth-only pass that clears the scene depth.</summary>
    private static bool VkPrepass(CommandList cl, VkColourState s, int groups, in Matrix4x4 vp, int* viewport, float near,
                                  float far, bool indirect)
    {
        Array.Clear(_gPrepassed, 0, groups);
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = s.ArenaUsed;
        var b = new VkBound { Fmt = -1, Tex = -1 };
        VkPassStart(side, viewport[2], viewport[3]);
        var depth = new DepthState(true, true, CompareOp.Less);
        var push = new VkBatchPush { AlphaCut = -1f };
        BufferId slots = indirect ? CullPassVk.Slots(CullPassVk.PhasePrepass) : s.Slots;
        for (int sweep = 0; _depthPrepass && sweep < 2; sweep++)
        {
            bool dis = sweep == 1;
            if (dis && !_prepassKey) break;
            VkProg p = s.Progs[(int)(dis ? VkPrograms.Id.DepthKey : VkPrograms.Id.Depth)]!;
            for (int g = 0; g < groups; g++)
            {
                if (ShaderFeatures.OutOfPrepass(_gFeat[g]) || _gCls[g] >= (byte)AlloyClass.Cutout || _gDis[g] != dis) continue;
                if (!VkBatch(side, ref b, p, g, VkTargets.Prepass, 0, depth, VkRaster(g, false), slots, 0, ref push, vp,
                             viewport, near, far, CullPassVk.PhasePrepass, indirect)) continue;
                _gPrepassed[g] = true;
            }
        }
        if (_depthPrepass && _prepassFeat)
        {
            VkProg p = s.Progs[(int)VkPrograms.Id.DepthFeat]!;
            for (int g = 0; g < groups; g++)
            {
                if (!FeatPrepassable(g) || (_gDis[g] && !_prepassKey) || !_gLit[g] || ContentPrograms.OwnDepth(_gFeat[g])) continue;
                push.AlphaCut = AlloyClasses.Discard(_gCls[g]);
                if (!VkBatch(side, ref b, p, g, VkTargets.Prepass, 0, depth, VkRaster(g, false), slots, 0, ref push, vp,
                             viewport, near, far, CullPassVk.PhasePrepass, indirect)) continue;
                _gPrepassed[g] = true;
            }
        }
        VkFlushPass(cl, side, arena0, new RenderPassDesc(default, new DepthAttachment(s.Depth, 0, 0, LoadOp.Clear, StoreOp.Store, 1f)));
        return true;
    }

    /// <summary><see cref="IssueClass"/>'s split: the plain run, then the keyhole dissolve partition at the class's tail.</summary>
    private static bool VkIssueClass(CommandList side, ref VkBound b, int from, int to, VkRange r, in Matrix4x4 vp, int* viewport,
                                     float near, float far, bool indirect)
    {
        int tail = from;
        while (tail < to && !_gDis[tail]) tail++;
        return VkIssue(side, ref b, from, tail, r, vp, viewport, near, far, indirect)
             & VkIssue(side, ref b, tail, to, r, vp, viewport, near, far, indirect);
    }

    /// <summary>One range of batches, deciding per batch as <see cref="Issue.IssueBatchesInner"/> does: the program, the
    /// cut, the blend pair, the depth rule, the wash offset, the cull. Water sits out (its pass is not on Vulkan); a
    /// content pair draws through the plain lit program until content programs port. False only on a refused bind.</summary>
    private static bool VkIssue(CommandList side, ref VkBound b, int from, int to, VkRange r, in Matrix4x4 vp, int* viewport,
                                float near, float far, bool indirect)
    {
        VkColourState s = _vkc;
        int viz = _capturing ? 0 : _lightViz;
        BufferId slots = indirect ? CullPassVk.Slots(CullPassVk.PhaseColour) : s.Slots;
        var push = new VkBatchPush { Solid = r.Interior ? 1 : 0 };
        for (int g = from; g < to; g++)
        {
            byte cls = _gCls[g];
            if (cls == (byte)AlloyClass.Water) { s.Skipped += _gLen[g]; continue; }
            bool lit = _gLit[g];
            bool content = Issue.HostedBody(lit, _gFeat[g]);
            bool oitOut = Issue.SitsOitOut(content, _gSrc[g], _gDst[g]);
            bool split = _gWash[g] == WashSplit && lit && g >= _gOpaqueEnd && (!oitOut || content && _gSrc[g] == 0 && _gDst[g] == 1);
            if (r.Interior && !split) continue;
            bool deferred = lit && r.Gbuf && (viz == 0 || viz == VizLightFrom) && !_capturing && (g < _gOpaqueEnd || r.Interior);
            VkProg p = s.Progs[(int)(lit ? deferred ? VkPrograms.Id.LitDeferred : VkPrograms.Id.Lit : VkPrograms.Id.Fullbright)]!;
            bool blend = AlloyClasses.Blends(cls) && !r.Interior;
            int blendKey = blend ? 1 + Math.Min((int)_gSrc[g], 7) * 8 + Math.Min((int)_gDst[g], 7) : 0;
            bool equal = r.MatchDepth && _gPrepassed[g];
            bool wash = _gWash[g] != 0 && !equal;
            CompareOp func = equal ? CompareOp.Equal : split && r.SplitRan && !r.Interior ? CompareOp.Less : CompareOp.LessEqual;
            bool test = !(blend && wash && !Renderer.WashTest.V);
            var depth = new DepthState(test, !equal && r.DepthMask, func);
            push.AlphaCut = AlloyClasses.Discard(cls);
            if (!VkBatch(side, ref b, p, g, r.Targets, blendKey, depth, VkRaster(g, wash), slots, lit ? 0 : _gTex[g], ref push,
                         vp, viewport, near, far, CullPassVk.PhaseColour, indirect)) continue;
            Counters._batches++;
            Counters._draws += _gLen[g];
            if (!r.Interior) Counters._drawsByClass[cls] += _gLen[g];
        }
        return true;
    }

    private static RasterState VkRaster(int g, bool wash) => new(_gNoCull[g] ? CullMode.None : CullMode.Back, false, wash);

    /// <summary>One batch: the program bound (its loose block captured) when it or the bound diffuse changed, the
    /// pipeline and dynamic state when they did, the per-batch constants, then the draw. False = sat out, counted.</summary>
    private static bool VkBatch(CommandList side, ref VkBound b, VkProg p, int g, VkTargets targets, int blend, DepthState depth,
                                RasterState raster, BufferId slots, int tex, ref VkBatchPush push, in Matrix4x4 vp, int* viewport,
                                float near, float far, int phase, bool indirect)
    {
        VkColourState s = _vkc;
        int fmt = _gVao[g] & ~DbArenaVaoBit;
        if ((uint)fmt >= MeshArena.Formats || MeshArena.VkVbo((MeshArena.Fmt)fmt).IsNull) { s.Skipped += _gLen[g]; return false; }
        PipelineId pipe = VkPipeline(p, fmt, targets, blend);
        if (pipe.IsNull) { s.Skipped += _gLen[g]; return false; }
        if (b.Prog != p || b.Tex != tex)
        {
            if (!VkBindProgram(side, p, vp, viewport, near, far, slots, tex)) { s.Skipped += _gLen[g]; return false; }
            b.Prog = p; b.Tex = tex;
        }
        if (pipe != b.Pipe) { side.SetPipeline(pipe); b.Pipe = pipe; b.StateSet = false; }
        if (!b.StateSet || depth != b.Depth) { side.SetDepthState(depth); b.Depth = depth; }
        if (!b.StateSet || raster != b.Raster) { side.SetRasterState(raster); b.Raster = raster; }
        b.StateSet = true;
        if (fmt != b.Fmt) { side.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)fmt)); b.Fmt = fmt; }
        int start = _gStart[g], len = _gLen[g];
        push.DrawBase = start;
        side.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<VkBatchPush>(in push)));
        if (indirect)
        {
            if (g >= CullPassVk.CompactBelow) side.DrawIndexedIndirect(CullPassVk.Commands(phase), (long)start * CullPass.CmdUints * sizeof(uint), len);
            else side.DrawIndexedIndirect(CullPassVk.Commands(phase), (long)start * CullPass.CmdUints * sizeof(uint), len,
                                          CullPassVk.Counts(phase), (long)g * sizeof(uint));
        }
        else
        {
            if (s.Firsts.Length < len) s.Firsts = new int[Math.Max(len, s.Firsts.Length * 2)];
            for (int k = 0; k < len; k++) s.Firsts[k] = (int)(_dOff[start + k] / sizeof(ushort));
            side.DrawIndexedMulti(_dCount.AsSpan(start, len), s.Firsts.AsSpan(0, len), _dBase.AsSpan(start, len));
        }
        s.Drawn += len;
        return true;
    }

    /// <summary>The encode into the composite: the linear scene colour to display space through the engine's gamma, at
    /// the display rectangle. ponytail: the post chain's place until it ports -- no tonemap, no TAA, no bloom.</summary>
    private static bool VkMakeEncode(VkBackend vk, VkColourState s, ExtenderLog log)
    {
        const string vs = "#version 460\nlayout(location = 0) out vec2 vUv;\nvoid main()\n{\n"
                        + "    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);\n    vUv = p;\n"
                        + "    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n}\n";
        string fs = "#version 460\nlayout(binding = 0) uniform sampler2D uHdr;\nlayout(location = 0) in vec2 vUv;\n"
                  + "layout(location = 0) out vec4 oColor;\nvoid main()\n{\n"
                  + "    vec3 c = pow(clamp(texture(uHdr, vUv).rgb, 0.0, 1.0), vec3(1.0 / "
                  + AlloyLights.SrgbGamma.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "));\n"
                  + "    oColor = vec4(c, 1.0);\n}\n";
        s.EncodeVs = vk.CreateShader(new ShaderDesc(ShaderStage.Vertex, null, vs, "vk encode vs"));
        s.EncodeFs = vk.CreateShader(new ShaderDesc(ShaderStage.Fragment, null, fs, "vk encode fs"));
        var d = new PipelineDesc { Vs = s.EncodeVs, Fs = s.EncodeFs, Label = "vk encode" };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba8;
        d.Targets.Samples = 1;
        s.Encode = vk.CreatePipeline(in d);
        if (s.Encode.IsNull) log.Always("AlloyRenderer: the Vulkan encode pipeline was REFUSED (logged above)");
        return !s.Encode.IsNull;
    }

    private static void VkEncode(CommandList cl, VkColourState s, ImageId composite, int* display)
    {
        Span<ColorAttachment> colors = stackalloc ColorAttachment[1];
        colors[0] = new ColorAttachment(composite, 0, 0, LoadOp.Clear, StoreOp.Store, new Vector4(0f, 0f, 0f, 1f));
        cl.BeginRenderPass(new RenderPassDesc(colors));
        cl.SetPipeline(s.Encode);
        cl.SetViewport(display[0], display[1], display[2], display[3]);
        ReadOnlySpan<ImageBinding> tex = [new(0, s.Hdr, s.Linear)];
        cl.BindSet(0, new BindSet(default, default, tex, default));
        cl.Draw(3);
        cl.EndRenderPass();
    }
}

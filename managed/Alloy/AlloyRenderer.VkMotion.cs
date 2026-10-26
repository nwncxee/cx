using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The mover sweep on Vulkan: every record whose model moved this frame drawn again into the velocity target after the
/// camera field, LEQUAL against the frame's depth, with its current and previous clip positions -- a posed skin
/// through both frames' palettes, a dangly part through its pre-solve positions -- as GL's sweep draws them.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The sweep's program and its two snapshot buffers on one device.</summary>
    private sealed class VkMvState
    {
        public VkBackend? Dev;
        public VkProg? Prog;
        public bool Tried, Live;
        public BufferId PrevBones, Dangly;
        public long PrevCap, DanglyCap;
        public CommandList? Side;
        public int PrevVp = -1, Jitter = -1, Size = -1, DanglyLoc = -1;
    }

    private static VkMvState _vkMv = new();

    /// <summary>The movers into <paramref name="vel"/>, after the camera field painted it. Nothing when no record moved.</summary>
    internal static void VkMoverMotion(VkBackend vk, CommandList cl, ImageId vel, int w, int h, Matrix4x4* vp,
                                       in Matrix4x4 prevVp, ExtenderLog log)
    {
        VkMvState s = _vkMv;
        if (s.Dev != vk) _vkMv = s = new VkMvState { Dev = vk };
        int n = _mvN;
        if (n == 0 || _capturing || !VkMvEnsure(vk, s, log)) return;
        VkColourState c = _vkc;
        if (_mvSkinned && _boneSlots > 0)
        {
            long bytes = (long)_boneSlots * BoneFloats * sizeof(float);
            if (!VkGrow(vk, ref s.PrevBones, ref s.PrevCap, bytes, "vk bones prev")) return;
            cl.UpdateBuffer(s.PrevBones, 0, new ReadOnlySpan<byte>(_bonePrevStage, (int)bytes));
        }
        else if (!VkGrow(vk, ref s.PrevBones, ref s.PrevCap, 64, "vk bones prev")) return;
        if (!VkGrow(vk, ref s.Dangly, ref s.DanglyCap, Math.Max((long)_dpUsed * sizeof(float), 64), "vk dangly prev")) return;
        if (_mvDangly && _dpUsed > 0) cl.UpdateBuffer(s.Dangly, 0, new ReadOnlySpan<byte>(_dpStage, _dpUsed * sizeof(float)));
        ReadOnlySpan<BufferBinding> own = [new(BindBonesPrev, s.PrevBones), new(BindDanglyPrev, s.Dangly)];
        VkProg p = s.Prog!;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        VkPassStart(side, w, h);
        int fmt = -1, dangly = -1, drawn = 0;
        var push = new VkBatchPush { AlphaCut = -1f };
        for (int k = 0; k < n; k++)
        {
            int d = _mvDraw[k], i = _dSlot[d];
            if (!DbSlot(i)) continue;
            int f = DbVaoKey(i);
            if ((uint)f >= MeshArena.Formats || MeshArena.VkVbo((MeshArena.Fmt)f).IsNull) continue;
            PipelineId pipe = VkPipeline(p, f, VkTargets.Velocity, 0);
            if (pipe.IsNull) continue;
            if (f != fmt)
            {
                fmt = f;
                side.SetPipeline(pipe);
                side.SetDepthState(new DepthState(true, false, CompareOp.LessEqual));
                side.SetRasterState(new RasterState(CullMode.None));
                side.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)f));
            }
            int want = DanglyPrevNow(i) ? i : -1;
            if (want != dangly || drawn == 0)
            {
                dangly = want;
                Array.Clear(p.Block);
                VkUniformShim.Capture(p.Members, p.Block);
                VkUniformShim.Set(p.Vp, *vp);
                VkUniformShim.Set(s.PrevVp, prevVp);
                VkUniformShim.Set(s.Jitter, [PostChain.JitterX, PostChain.JitterY]);
                VkUniformShim.Set(s.Size, [(float)w, h]);
                VkUniformShim.Set(s.DanglyLoc, want >= 0 ? (ReadOnlySpan<int>)[_oDpBase[i], _oDpN[i]] : [0, 0]);
                VkUniformShim.EndCapture();
                int loose = VkArena(p.Block);
                if (loose < 0) return;
                VkBindSet(side, p, c, loose, -1, c.Slots, 0, default, own);
            }
            push.DrawBase = d;
            side.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<VkBatchPush>(in push)));
            side.DrawIndexed(_dCount[d], 1, (int)(_dOff[d] / sizeof(ushort)), _dBase[d]);
            drawn++;
        }
        if (drawn == 0) return;
        ReadOnlySpan<ColorAttachment> colors = [new(vel, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
        _mvDrawn += drawn;
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan mover velocities are LIVE -- {drawn} moving record(s) over the camera field");
        }
    }

    /// <summary>The sweep's program, made once per device; a refusal leaves movers on camera-only vectors, said once.</summary>
    private static bool VkMvEnsure(VkBackend vk, VkMvState s, ExtenderLog log)
    {
        if (s.Tried) return s.Prog != null;
        s.Tried = true;
        s.Side = new CommandList("vulkan mover motion", 1 << 16, log);
        s.Prog = VkBuild(vk, VkPrograms.Id.Motion, log);
        const uint name = VkUniformShim.ProgramBase + (uint)VkPrograms.Id.Motion;
        if (s.Prog != null)
            VkUniformShim.Resolve(s.Prog.Names, () =>
            {
                s.PrevVp = GlProgram.Loc(name, "uPrevVP"); s.Jitter = GlProgram.Loc(name, "uJitter");
                s.Size = GlProgram.Loc(name, "uSize"); s.DanglyLoc = GlProgram.Loc(name, "uDangly");
            });
        else log.Always("AlloyRenderer: the Vulkan mover velocity program was REFUSED (logged above) -- movers keep camera-only vectors");
        return s.Prog != null;
    }
}

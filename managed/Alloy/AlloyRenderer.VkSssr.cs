using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The FidelityFX SSSR trial on the reflections' slot. The record pair is unpacked into the normal-and-roughness image AMD
/// reads, the velocity is painted early because the post stack's comes after this slot, and AMD's output is added on as
/// (reflection - probe) x weight, the same correction Alloy's resolve writes. Deliberately Linux-only: it never merges.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The trial's programs and targets, on <see cref="VkSsrState.Trial"/> so the device takes them with it.</summary>
    internal sealed class VkSssrState
    {
        public string? Refused;
        public bool Tried, PrevOn, Live;
        public Matrix4x4 PrevClean;
        public GuiVkProg? Vel, Unpack, Compose;
        public PipelineId VelPipe, UnpackPipe, ComposePipe;
        public ImageId Motion, Normal, Out;
        public int W, H;
        public int VInvVp = -1, VPrevVp = -1, VSize = -1, VJitter = -1;
        public int UInvProj = -1, UInvView = -1, UCap = -1;
        public int CInvProj = -1, CView = -1, CInvView = -1, CCap = -1, CProbeMips = -1;
    }

    /// <summary>The trial for this frame; false lets Alloy's own pass draw, after a refusal said once why.</summary>
    private static bool VkSssrRun(VkBackend vk, CommandList cl, VkSsrState s, in Matrix4x4 vp, in Matrix4x4 view, in Matrix4x4 proj,
                                  in Matrix4x4 invProj, in Matrix4x4 invView, float near, float far, ExtenderLog log)
    {
        VkSssrState t = s.Trial;
        if (t.Refused == null && vk.SssrReady(s.W, s.H) is string no) t.Refused = no;
        if (t.Refused == null) VkSssrEnsure(vk, s, t);
        if (t.Refused != null)
        {
            if (!t.Tried || !t.Live)
            {
                t.Live = true;
                log.Always($"AlloyRenderer: the SSSR trial is OFF for the session -- {t.Refused}. Alloy's reflections draw.");
            }
            return false;
        }
        if (!Matrix4x4.Invert(vp, out Matrix4x4 invVp)) return false;
        VkColourState c = _vkc;
        Matrix4x4 prev = t.PrevOn ? t.PrevClean : s.VpClean;
        int arena0 = c.ArenaUsed;
        GuiVkProg ve = t.Vel!, un = t.Unpack!, co = t.Compose!;
        Array.Clear(ve.Block);
        LooseBlock.Capture(ve.Members, ve.Block);
        LooseBlock.Set(t.VInvVp, invVp);
        LooseBlock.Set(t.VPrevVp, prev);
        LooseBlock.Set(t.VSize, [(float)s.W, s.H]);
        LooseBlock.Set(t.VJitter, [PostChain.JitterX, PostChain.JitterY]);
        LooseBlock.EndCapture();
        int veAt = VkArena(ve.Block);
        Array.Clear(un.Block);
        LooseBlock.Capture(un.Members, un.Block);
        LooseBlock.Set(t.UInvProj, invProj);
        LooseBlock.Set(t.UInvView, invView);
        LooseBlock.Set(t.UCap, Lighting.SsrRough.V);
        LooseBlock.EndCapture();
        int unAt = VkArena(un.Block);
        Array.Clear(co.Block);
        LooseBlock.Capture(co.Members, co.Block);
        LooseBlock.Set(t.CInvProj, invProj);
        LooseBlock.Set(t.CView, view);
        LooseBlock.Set(t.CInvView, invView);
        LooseBlock.Set(t.CCap, Lighting.SsrRough.V);
        LooseBlock.Set(t.CProbeMips, (float)VkProbeMips);
        LooseBlock.EndCapture();
        int coAt = VkArena(co.Block);
        if (veAt < 0 || unAt < 0 || coAt < 0) return false;
        int len = (c.ArenaUsed - arena0 + 3) & ~3;
        cl.UpdateBuffer(c.Arena, arena0, c.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));

        cl.BeginLabel("sssr inputs"u8);
        // the one level CxRecRead reads: AMD builds its own pyramid
        VkSpdChain(cl, s.HiZ, c.Depth, s.Point, s.Pyr, s.W, s.H, 1, new Vector2(near, far));
        ReadOnlySpan<ImageId> read = [s.Pyr, s.RecN, s.RecW, c.Depth];
        cl.ReadImages(read);
        VkSsrDraw(cl, ve, t.VelPipe, veAt, [new(t.Motion, 0, 0, LoadOp.DontCare, StoreOp.Store, default)], s.W, s.H);
        Matrix4x4 vpCopy = vp;
        VkMoverMotion(vk, cl, t.Motion, s.W, s.H, &vpCopy, prev, log);
        VkGrassMotion(vk, cl, t.Motion, s.W, s.H, &vpCopy, prev);
        cl.ReadImages(read);
        VkSsrDraw(cl, un, t.UnpackPipe, unAt, [new(t.Normal, 0, 0, LoadOp.DontCare, StoreOp.Store, default)], s.W, s.H);
        cl.EndLabel();
        cl.BeginLabel("sssr"u8);
        ImageId env = VkProbeLive ? _vkProbe.Tex : c.TexCube;
        cl.Sssr(new SssrCmd(c.Hdr, c.Depth, t.Motion, t.Normal, env, t.Out,
                            Ssr.AmdFlipInverse(invVp), Ssr.AmdFlip(proj), Ssr.AmdFlipInverse(invProj), view, invView, Ssr.AmdFlip(prev),
                            1f / s.W, 1f / s.H, Lighting.SssrThick.V, Lighting.SssrStability.V, Lighting.SsrRough.V, Lighting.SssrSamples.V, Lighting.SssrGuided.V, EnvProbe.Origin));
        cl.EndLabel();
        cl.BeginLabel("sssr composite"u8);
        cl.ReadImages([t.Out, s.Pyr, s.RecN, s.RecW, c.Depth]);
        VkSsrDraw(cl, co, t.ComposePipe, coAt, [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)], s.W, s.H);
        cl.EndLabel();
        t.PrevClean = s.VpClean;
        t.PrevOn = true;
        if (!t.Live)
        {
            t.Live = true;
            log.Always($"AlloyRenderer: the SSSR trial is LIVE at {s.W}x{s.H}, {Lighting.SssrSamples.V} ray(s) per quad");
        }
        return true;
    }

    /// <summary>The programs once and the targets at the frame's size; a refusal lands in <see cref="VkSssrState.Refused"/>.</summary>
    private static void VkSssrEnsure(VkBackend vk, VkSsrState s, VkSssrState t)
    {
        if (!t.Tried)
        {
            t.Tried = true;
            ExtenderLog log = _log!;
            t.Vel = PostChainVk.Program(vk, PostChain.FullscreenVs, MotionVectors.CameraFs, "sssr velocity", p =>
            {
                t.VInvVp = p.Loc("uInvVP"); t.VPrevVp = p.Loc("uPrevVP"); t.VSize = p.Loc("uSize"); t.VJitter = p.Loc("uJitter");
            }, log);
            t.Unpack = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_sssrunp_f.glsl"), "sssr unpack", p =>
            {
                t.UInvProj = p.Loc("uInvProj"); t.UInvView = p.Loc("uInvView"); t.UCap = p.Loc("uCap");
            }, log);
            t.Compose = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_sssrcmp_f.glsl"), "sssr composite", p =>
            {
                t.CInvProj = p.Loc("uInvProj"); t.CView = p.Loc("uView"); t.CInvView = p.Loc("uInvView"); t.CCap = p.Loc("uCap"); t.CProbeMips = p.Loc("uProbeMips");
            }, log);
            if (t.Vel != null) t.VelPipe = VkSsrPipe(vk, t.Vel, Format.Rg16F, false);
            if (t.Unpack != null) t.UnpackPipe = VkSsrPipe(vk, t.Unpack, Format.Rgba16F, false);
            if (t.Compose != null)
            {
                var d = new PipelineDesc { Vs = t.Compose.Vs, Fs = t.Compose.Fs, Label = "vk sssr composite" };
                d.Targets.ColorCount = 1;
                d.Targets.Colors[0] = Format.Rgba16F;
                d.Targets.Samples = 1;
                d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
                t.ComposePipe = vk.CreatePipeline(in d);
            }
            if (t.VelPipe.IsNull || t.UnpackPipe.IsNull || t.ComposePipe.IsNull) { t.Refused = "a program or pipeline was refused (logged above)"; return; }
        }
        if (!t.Out.IsNull && t.W == s.W && t.H == s.H) return;
        vk.Destroy(t.Motion); vk.Destroy(t.Normal); vk.Destroy(t.Out);
        const ImageUse attach = ImageUse.Sampled | ImageUse.ColorTarget;
        t.Motion = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg16F, s.W, s.H, 1, 1, 1, 1, attach, "vk sssr velocity"));
        t.Normal = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, s.W, s.H, 1, 1, 1, 1, attach | ImageUse.CopySrc, "vk sssr normal"));
        t.Out = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, s.W, s.H, 1, 1, 1, 1, ImageUse.Sampled | ImageUse.Storage | ImageUse.CopyDst, "vk sssr output"));
        t.W = s.W; t.H = s.H;
        t.PrevOn = false;
        if (t.Motion.IsNull || t.Normal.IsNull || t.Out.IsNull) t.Refused = $"a {s.W}x{s.H} target was refused (logged above)";
    }
}

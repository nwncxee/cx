using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The mesh path golden's fixture: a fixed precipitation field drawn through one path into a small target and read back,
/// the camera, the fall and a roof in the cover map held still, so the compute and mesh paths can be laid side by side.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The fixture target's edge in pixels.</summary>
    private const int VkWxFixtureSide = 64;

    /// <summary>Lattice points the fixture walks.</summary>
    private const int VkWxFixtureCount = 150000;

    /// <summary>A fixed field of <paramref name="kind"/> (1 rain, 2 snow, 3 drips) drawn through <paramref name="path"/>:
    /// the coverage of every pixel of the target, read back. Null when a step was refused.</summary>
    internal static float[]? VkWxCoverage(VkBackend vk, ExtenderLog log, Weather.WxPath path, int kind)
    {
        const int n = VkWxFixtureSide;
        if (!VkEnsure(vk, n, n, log)) return null;
        VkColourState c = _vkc;
        var s = new VkWxState { Dev = vk };
        int k = kind == 2 ? 1 : 0;
        bool built = path == Weather.WxPath.Mesh ? VkWxMeshEnsure(vk, s, k, log)
                                                 : VkWxCullEnsure(vk, s, log) && VkWxEnsure(vk, s, k, log);
        ImageId col = VkTarget(vk, Format.Rgba16F, n, n, "golden weather colour");
        ImageId cover = vk.CreateImage(new ImageDesc(ImageKind.Tex2DArray, Format.D32F, n, n, 1, 1, 1, 1,
                                                     ImageUse.Sampled | ImageUse.CopyDst, "golden weather cover"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(n * n * 8, BufferUse.CopyDst, MemoryKind.Readback, "golden weather read"));
        float[]? got = null;
        if (built && !col.IsNull && !cover.IsNull && !rb.IsNull)
        {
            // a roof over the half of the cover view west of the camera, 3 m up; open sky over the rest
            var roof = new float[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) roof[y * n + x] = x < n / 2 ? VkWxFixtureDepth(3f) : 1f;
            vk.Upload(cover, 0, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(roof.AsSpan()));
            Vector3 eye = new(0.5f, 0f, 1.7f);
            Weather.Hold(new Weather.WxHoldState { Fall = new Vector3(0f, 0f, -1f), Phase = 3.0, CoverVp = VkWxFixtureCoverVp(eye) });
            Vector3 saved = _eyePos;
            _eyePos = eye;
            try { got = VkWxFixtureDraw(vk, c, s, path, k, kind, col, cover, rb, log); }
            finally { _eyePos = saved; Weather.Hold(null); }
        }
        vk.Destroy(col); vk.Destroy(cover); vk.Destroy(rb);
        VkWxFixtureFree(vk, s);
        return got;
    }

    /// <summary>Everything the fixture's own <see cref="VkWxState"/> made on <paramref name="vk"/>: the cull stage and its
    /// buffers, and each program's shaders and pipelines on either path.</summary>
    private static void VkWxFixtureFree(VkBackend vk, VkWxState s)
    {
        if (s.Cull != null) { vk.Destroy(s.Cull.Pipe); vk.Destroy(s.Cull.Cs); }
        vk.Destroy(s.Drops); vk.Destroy(s.Cmd); vk.Destroy(s.CmdRead);
        foreach (PipelineId pipe in s.Pipe) vk.Destroy(pipe);
        foreach (VkProg? p in s.Prog) VkWxFixtureFree(vk, p);
        if (s.MeshState is not { } m) return;
        foreach (PipelineId pipe in m.Pipe) vk.Destroy(pipe);
        foreach (PipelineId pipe in m.LatePipe) vk.Destroy(pipe);
        foreach (VkProg? p in m.Prog) VkWxFixtureFree(vk, p);
    }

    private static void VkWxFixtureFree(VkBackend vk, VkProg? p)
    {
        if (p == null) return;
        vk.Destroy(p.Vs); vk.Destroy(p.Fs); vk.Destroy(p.Ts); vk.Destroy(p.Ms);
    }

    /// <summary>The fixture's one frame: the field into <paramref name="col"/> over a far depth, its alpha read back.</summary>
    private static float[]? VkWxFixtureDraw(VkBackend vk, VkColourState c, VkWxState s, Weather.WxPath path, int k, int kind,
                                            ImageId col, ImageId cover, BufferId rb, ExtenderLog log)
    {
        const int n = VkWxFixtureSide;
        // straight along the roof's lip, so the drips under it and the open rain beside it both fill the view
        Matrix4x4 view = Matrix4x4.CreateLookAt(_eyePos, _eyePos + new Vector3(0f, 1f, -0.15f), Vector3.UnitZ);
        Matrix4x4 proj = WorldDepth.Fold(AlloyCamera.Perspective(60f, 1f, 0.1f, 100f));
        int* viewport = stackalloc int[4] { 0, 0, n, n };
        uint coverAt = 0;
        VkProg? prog = path == Weather.WxPath.Mesh ? s.MeshState!.Prog[k] : null;
        foreach (VkOpaque o in path == Weather.WxPath.Mesh ? prog!.Opaques : s.Cull!.Opaques)
            if (o.Name == "uCover") coverAt = o.Binding;
        ReadOnlySpan<ImageBinding> own = [new ImageBinding(coverAt, cover, c.Shadow)];
        int fall = kind == 3 ? 0 : VkWxFixtureCount, drips = kind == 3 ? VkWxFixtureCount : 0;
        c.ArenaUsed = 0;
        var cl = new CommandList("golden weather", 1 << 16, log);
        // the depth copy the contact fade reads: cleared to the far plane, so nothing stands in front of a drop
        ReadOnlySpan<ColorAttachment> none = [];
        cl.BeginRenderPass(new RenderPassDesc(none, new DepthAttachment(c.Depth, 0, 0, LoadOp.Clear, StoreOp.Store, WorldDepth.Clear)));
        cl.EndRenderPass();
        cl.CopyImage(new CopyImageCmd(c.Depth, new ImageRegion(0, 0, 0, 0), c.DepthRead, new ImageRegion(0, 0, 0, 0), n, n, 1));
        ReadOnlySpan<ImageId> read = [c.DepthRead, cover];
        cl.ReadImages(read);
        CommandList side = new("golden weather draw", 1 << 14, log);
        side.SetViewport(0, 0, n, n);
        bool ok;
        if (path == Weather.WxPath.Mesh)
            ok = VkWxMeshDraw(vk, side, s, c, k, kind, kind == 3 ? drips : fall, false, &view, &proj, viewport, 0.1f, 100f, false, log, own);
        else
            ok = VkWxCull(cl, s, c, kind == 3 ? 1 : kind, fall, drips, 0f, view, proj, viewport, 0.1f, 100f, false, own)
                 && VkWxDraw(side, s, c, k, kind, kind == 3 ? 1 : 0, view, proj, viewport, 0.1f, 100f, false, own);
        if (!ok) return null;
        ReadOnlySpan<ColorAttachment> colors = [new(col, 0, 0, LoadOp.Clear, StoreOp.Store, default)];
        VkFlushPass(cl, side, 0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)));
        cl.CopyImageToBuffer(new CopyToBufferCmd(col, new ImageRegion(0, 0, 0, 0), n, n, rb, 0));
        var bytes = new byte[n * n * 8];
        if (!vk.Submit(cl) || !vk.ReadBack(rb, bytes)) return null;
        var alpha = new float[n * n];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = (float)BitConverter.ToHalf(bytes, i * 8 + 6);
        return alpha;
    }

    /// <summary>Half the fixture cover view's reach, metres: across and from its top to its floor.</summary>
    private const float VkWxFixtureReach = 20f;

    /// <summary>The fixture's cover view: straight down over the camera, 40 m across, from 20 m up to 20 m down.</summary>
    private static Matrix4x4 VkWxFixtureCoverVp(Vector3 eye)
    {
        const float r = VkWxFixtureReach;
        return AlloyCamera.Ortho(eye.X - r, eye.X + r, eye.Y - r, eye.Y + r, -r, r);
    }

    /// <summary>The cover map's stored depth for a surface <paramref name="height"/> metres up in the fixture's view.</summary>
    private static float VkWxFixtureDepth(float height) => (VkWxFixtureReach - height) / (2f * VkWxFixtureReach);
}

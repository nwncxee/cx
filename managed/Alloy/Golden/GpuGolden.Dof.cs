using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The harness behind the depth of field goldens: real FidelityFX DoF dispatches through <see cref="DepthOfField.Record"/>
/// over a frame built by hand. Depth is worked out here from the projection's own terms and never by the pass, so the
/// pass's decode has an oracle, and the option-off regression lives here too.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>One pixel of a golden's frame.</summary>
    private readonly record struct DofPx(Half R, Half G, Half B, Half A);

    /// <summary>The frame's edge, and the near and far planes it projects through. Large enough that the blur at
    /// confusion 1 clears a one-texel checker.</summary>
    private const int DofSide = 1024;
    private const float DofNearPlane = 1f, DofFarPlane = 1000f;

    /// <summary>What a golden sets the group's knobs to; the defaults are vanilla's, with the effect on.</summary>
    private readonly record struct DofSet(bool Enabled = true, float Amount = 1f, float DeadZone = 3f, int Focus = 1, bool NeverBlur = true);

    /// <summary>Runs <paramref name="body"/> with the group's knobs at <paramref name="set"/>, and puts them back.</summary>
    private static T DofWith<T>(in DofSet set, Func<T> body)
    {
        (bool e, float a, float d, int f, bool n) = (Dof.Enabled.V, Dof.Amount.V, Dof.DeadZone.V, Dof.Focus.V, Dof.NeverBlur.V);
        try
        {
            Dof.Enabled.V = set.Enabled; Dof.Amount.V = set.Amount; Dof.DeadZone.V = set.DeadZone; Dof.Focus.V = set.Focus; Dof.NeverBlur.V = set.NeverBlur;
            return body();
        }
        finally { Dof.Enabled.V = e; Dof.Amount.V = a; Dof.DeadZone.V = d; Dof.Focus.V = f; Dof.NeverBlur.V = n; }
    }

    /// <summary>Reversed depth of a point <paramref name="z"/> metres from the eye: near plane 1, far plane 0.</summary>
    private static float DofDepthOf(float z) => (DofFarPlane * DofNearPlane / z - DofNearPlane) / (DofFarPlane - DofNearPlane);

    /// <summary>A one-texel checker of 0 and 1.</summary>
    private static float DofChecker(int x, int y) => ((x ^ y) & 1) == 0 ? 0f : 1f;

    /// <summary>One frame through the pass: grey-or-coloured <paramref name="colour"/> and view distances
    /// <paramref name="z"/>, per pixel of a <see cref="DofSide"/> frame, with depth at <paramref name="depthSide"/>.
    /// RGBA of the image the frame leaves the pass in, or null when anything was refused (logged).</summary>
    private static DofPx[]? DofRun(VkBackend vk, ExtenderLog log, Func<int, int, (float R, float G, float B)> colour, Func<int, int, float> z,
                                  DepthOfField.Scene scene, DofSet set = default, int depthSide = DofSide)
    {
        if (set == default) set = new DofSet(Enabled: true);
        return DofWith(in set, () => DofFrame(vk, log, colour, z, scene, depthSide));
    }

    private static DofPx[]? DofFrame(VkBackend vk, ExtenderLog log, Func<int, int, (float R, float G, float B)> colour, Func<int, int, float> z,
                                     in DepthOfField.Scene scene, int depthSide)
    {
        ImageId hdr = PostImage(vk, Format.Rgba16F, DofSide, DofSide, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden dof colour");
        ImageId depth = PostImage(vk, Format.R32F, depthSide, depthSide, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden dof depth");
        BufferId rb = vk.CreateBuffer(new BufferDesc(DofSide * DofSide * 8L, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden dof read"));
        try
        {
            if (hdr.IsNull || depth.IsNull || rb.IsNull) { log.Always("GpuGolden: dof, an image or the readback was refused (logged above)"); return null; }
            var px = new DofPx[DofSide * DofSide];
            for (int y = 0; y < DofSide; y++)
                for (int x = 0; x < DofSide; x++)
                {
                    (float r, float g, float b) = colour(x, y);
                    px.AsSpan(y * DofSide, DofSide)[x] = new DofPx((Half)r, (Half)g, (Half)b, (Half)1f);
                }
            var d = new float[depthSide * depthSide];
            for (int y = 0; y < depthSide; y++)
                for (int x = 0; x < depthSide; x++)
                    d.AsSpan(y * depthSide, depthSide)[x] = DofDepthOf(z((x * 2 + 1) * DofSide / (2 * depthSide), (y * 2 + 1) * DofSide / (2 * depthSide)));
            if (!vk.Upload(hdr, 0, 0, MemoryMarshal.AsBytes(px.AsSpan())) || !vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(d.AsSpan())))
            { log.Always("GpuGolden: dof, an upload was refused (logged above)"); return null; }
            DepthOfField.Forget();
            ImageId into = hdr;
            var cl = new CommandList("golden dof", 1 << 12, log);
            DepthOfField.Record(vk, cl, ref into, depth, DofSide, DofSide, in scene, 1f / 60f, DofNearPlane, DofFarPlane, 1f, log);
            if (!vk.Submit(cl)) { log.Always("GpuGolden: dof, the frame was refused (logged above)"); return null; }
            if (PostRead(vk, log, into, DofSide, DofSide, 8, rb) is not { } bytes) { log.Always("GpuGolden: dof, the read-back was refused (logged above)"); return null; }
            return MemoryMarshal.Cast<byte, DofPx>(bytes).ToArray();
        }
        finally
        {
            DepthOfField.Drop(vk);
            vk.Destroy(rb); vk.Destroy(depth); vk.Destroy(hdr);
        }
    }

    /// <summary>Channel <paramref name="k"/> at a pixel of what <see cref="DofRun"/> returned.</summary>
    private static float DofAt(DofPx[] o, int x, int y, int k = 0)
    {
        DofPx p = o.AsSpan(y * DofSide, DofSide)[x];
        return (float)(k == 0 ? p.R : k == 1 ? p.G : p.B);
    }

    /// <summary>The largest red difference between neighbours over a block of the frame: 1 on an untouched checker, near 0
    /// once the checker has been blurred away.</summary>
    private static float DofContrast(DofPx[] o, int x0, int x1, int y0, int y1)
    {
        float most = 0f;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                most = MathF.Max(most, MathF.Abs(DofAt(o, x + 1, y) - DofAt(o, x, y)));
        return most;
    }

    /// <summary>The depth <see cref="DepthOfField.AskDepths"/> reads under a mouse at (<paramref name="mx"/>, <paramref name="my"/>),
    /// over a half-size depth with the character at 8 m on the left and a wall at 30 m on the right; NaN when refused.</summary>
    private static float DofProbe(VkBackend vk, ExtenderLog log, int mx, int my)
    {
        int side = DofSide / 2;
        ImageId depth = PostImage(vk, Format.R32F, side, side, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden dof probe depth");
        try
        {
            var d = new float[side * side];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                    d.AsSpan(y * side, side)[x] = DofDepthOf(x < side / 2 ? 8f : 30f);
            if (depth.IsNull || !vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(d.AsSpan())))
            { log.Always("GpuGolden: dof, the probe's depth was refused (logged above)"); return float.NaN; }
            DepthOfField.Forget();
            var cl = new CommandList("golden dof probe", 1 << 10, log);
            DepthOfField.AskDepths(vk, cl, depth, DofSide, DofSide, side, side, DofCamera(8f, 45f), mx, my, DofNearPlane, DofFarPlane);
            if (!vk.Submit(cl) || !vk.WaitSubmitted()) { log.Always("GpuGolden: dof, the probe was refused (logged above)"); return float.NaN; }
            return DepthOfField.MouseDepth;
        }
        finally
        {
            DepthOfField.Drop(vk);
            vk.Destroy(depth);
        }
    }

    /// <summary>The camera a scene puts behind a character at <paramref name="dist"/> metres, no conversation.</summary>
    private static DepthOfField.Scene DofCamera(float dist, float pitch, bool dialog = false, float mouseDepth = 0f)
        => new(dist, pitch, dialog, false, mouseDepth, 0f, 0f, 0f);

    static partial void DofApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log);

    /// <summary>A regression, not an approved test, since it held before the effect existed: the setting off leaves the
    /// frame in the image it came in, every pixel as it was. Oracle: the fixture's own checker.</summary>
    private static void DofRegressionSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        DofPx[]? off = DofRun(vk, log, static (x, y) => (DofChecker(x, y), 0f, 0f), static (x, y) => 40f, DofCamera(8f, 70f), new DofSet(Enabled: false));
        check.Expect(off != null, "dof: the setting-off frame ran");
        if (off != null) check.Expect(DofContrast(off, 400, 600, 400, 600) > 0.99f, "dof: with the setting off nothing is blurred");
    }
}

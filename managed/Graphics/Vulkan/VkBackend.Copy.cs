using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Rhi;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The seam's two image copies on Vulkan: <see cref="Op.CopyImage"/> texel for texel and <see cref="Op.BlitImage"/>
/// scaled. Both move their images into the transfer layouts themselves and leave them there -- the next bind or
/// attachment transitions them on -- and both refuse inside a render pass, where Vulkan forbids a barrier.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>A block of <paramref name="c"/>'s source into its destination, texel for texel, one mip and one
    /// layer of each. The two must share an aspect: a depth image copies to a depth image, colour to colour.</summary>
    private bool CopyImg(in CopyImageCmd c)
    {
        if (_inPass) return Fault("CopyImage inside a render pass -- a copy is not a draw, and its barriers cannot run there");
        if (!_images.TryGet(c.Src.Value, out VkImg src)) return Fault("CopyImage: stale source image");
        if (!_images.TryGet(c.Dst.Value, out VkImg dst)) return Fault("CopyImage: stale destination image");
        if (c.W <= 0 || c.H <= 0 || c.D <= 0) return Fault($"CopyImage {src.Desc.Label} to {dst.Desc.Label}: a {c.W}x{c.H}x{c.D} block");
        if (Aspect(src.Desc.Fmt) != Aspect(dst.Desc.Fmt))
            return Fault($"CopyImage {src.Desc.Label} ({src.Desc.Fmt}) to {dst.Desc.Label} ({dst.Desc.Fmt}): different aspects");
        // a size-compatible copy between a block format and one texel per block counts its extent in the SOURCE's texels
        bool sc = src.Desc.Fmt.IsCompressed(), dc = dst.Desc.Fmt.IsCompressed();
        if (sc != dc && (sc ? src.Desc.Fmt : dst.Desc.Fmt).BlockBytes() != TexelBytes(sc ? dst.Desc.Fmt : src.Desc.Fmt))
            return Fault($"CopyImage {src.Desc.Label} ({src.Desc.Fmt}) to {dst.Desc.Label} ({dst.Desc.Fmt}): a block is not one texel's size");
        int dw = sc == dc ? c.W : dc ? c.W * 4 : c.W / 4, dh = sc == dc ? c.H : dc ? c.H * 4 : c.H / 4;
        if (!Inside(src, c.From, c.W, c.H, c.D, out string why)) return Fault($"CopyImage: the source block is {why} of {src.Desc.Label}");
        if (!Inside(dst, c.To, dw, dh, c.D, out why)) return Fault($"CopyImage: the destination block is {why} of {dst.Desc.Label}");
        Transition(src, VkImageLayout.TransferSrcOptimal);
        Transition(dst, VkImageLayout.TransferDstOptimal);
        var r = new VkImageCopy
        {
            srcSubresource = Sub(src, c.From), srcOffset = At(src, c.From),
            dstSubresource = Sub(dst, c.To), dstOffset = At(dst, c.To),
            extent = new VkExtent3D { width = (uint)c.W, height = (uint)c.H, depth = (uint)c.D },
        };
        _vkd.vkCmdCopyImage(_cmd, src.Image, VkImageLayout.TransferSrcOptimal, dst.Image,
                            VkImageLayout.TransferDstOptimal, 1, &r);
        return true;
    }

    /// <summary>A scaled copy of <paramref name="c"/>'s source rectangle into its destination rectangle, mip 0 and
    /// layer 0. A depth blit filters NEAREST whatever the command asked: Vulkan allows a linear filter on the colour
    /// aspect alone.</summary>
    private bool BlitImg(in BlitImageCmd c)
    {
        if (_inPass) return Fault("BlitImage inside a render pass -- a blit is not a draw, and its barriers cannot run there");
        if (!_images.TryGet(c.Src.Value, out VkImg src)) return Fault("BlitImage: stale source image");
        if (!_images.TryGet(c.Dst.Value, out VkImg dst)) return Fault("BlitImage: stale destination image");
        if (c.From.W <= 0 || c.From.H <= 0 || c.To.W <= 0 || c.To.H <= 0)
            return Fault($"BlitImage {src.Desc.Label} to {dst.Desc.Label}: {c.From.W}x{c.From.H} to {c.To.W}x{c.To.H}");
        if (src.Desc.Samples > 1)
            return Fault($"BlitImage: {src.Desc.Label} is multisampled, and a resolve takes vkCmdResolveImage, which is not on this backend");
        if (Aspect(src.Desc.Fmt) != Aspect(dst.Desc.Fmt))
            return Fault($"BlitImage {src.Desc.Label} ({src.Desc.Fmt}) to {dst.Desc.Label} ({dst.Desc.Fmt}): different aspects");
        Transition(src, VkImageLayout.TransferSrcOptimal);
        Transition(dst, VkImageLayout.TransferDstOptimal);
        var b = new VkImageBlit
        {
            srcSubresource = new VkImageSubresourceLayers { aspectMask = Aspect(src.Desc.Fmt), layerCount = 1 },
            dstSubresource = new VkImageSubresourceLayers { aspectMask = Aspect(dst.Desc.Fmt), layerCount = 1 },
        };
        b.srcOffsets[0] = new VkOffset3D { x = c.From.X, y = c.From.Y, z = 0 };
        b.srcOffsets[1] = new VkOffset3D { x = c.From.X + c.From.W, y = c.From.Y + c.From.H, z = 1 };
        b.dstOffsets[0] = new VkOffset3D { x = c.To.X, y = c.To.Y, z = 0 };
        b.dstOffsets[1] = new VkOffset3D { x = c.To.X + c.To.W, y = c.To.Y + c.To.H, z = 1 };
        VkFilter f = c.Filter == Filter.Linear && !src.Desc.Fmt.IsDepth() ? VkFilter.Linear : VkFilter.Nearest;
        _vkd.vkCmdBlitImage(_cmd, src.Image, VkImageLayout.TransferSrcOptimal, dst.Image,
                            VkImageLayout.TransferDstOptimal, 1, &b, f);
        return true;
    }

    /// <summary>One region's subresource: its mip, and the layer <see cref="ImageRegion.Z"/> names for an array or a
    /// cube (a 3D image has one layer and spends Z on the slice instead).</summary>
    private static VkImageSubresourceLayers Sub(VkImg img, in ImageRegion r) => new()
    {
        aspectMask = Aspect(img.Desc.Fmt), mipLevel = (uint)r.Mip,
        baseArrayLayer = (uint)(img.Desc.Kind == ImageKind.Tex3D ? 0 : r.Z), layerCount = 1,
    };

    /// <summary>One region's texel origin; a 3D image's <see cref="ImageRegion.Z"/> is its slice.</summary>
    private static VkOffset3D At(VkImg img, in ImageRegion r)
        => new() { x = r.X, y = r.Y, z = img.Desc.Kind == ImageKind.Tex3D ? r.Z : 0 };

    /// <summary>Does the block fit inside <paramref name="img"/>? Validation would say so too, but a named refusal
    /// points at the pass rather than at the driver.</summary>
    private static bool Inside(VkImg img, in ImageRegion r, int w, int h, int d, out string why)
    {
        why = "";
        if (r.Mip < 0 || r.Mip >= img.Desc.Mips) { why = $"on mip {r.Mip}, past the {img.Desc.Mips}"; return false; }
        int layers = img.Desc.Kind is ImageKind.Cube or ImageKind.CubeArray ? img.Desc.Layers * 6
                   : img.Desc.Kind == ImageKind.Tex3D ? 1 : img.Desc.Layers;
        int lw = Math.Max(img.Desc.W >> r.Mip, 1), lh = Math.Max(img.Desc.H >> r.Mip, 1);
        int ld = img.Desc.Kind == ImageKind.Tex3D ? Math.Max(img.Desc.D >> r.Mip, 1) : 1;
        if (img.Desc.Kind == ImageKind.Tex3D)
        {
            if (r.Z < 0 || r.Z + d > ld) { why = $"slices {r.Z}..{r.Z + d} of {ld}"; return false; }
        }
        else if (r.Z < 0 || r.Z >= layers || d != 1) { why = $"layer {r.Z} of {layers} with depth {d}"; return false; }
        if (r.X < 0 || r.Y < 0 || r.X + w > lw || r.Y + h > lh)
        {
            why = $"({r.X}, {r.Y}) + {w}x{h}, outside the mip's {lw}x{lh}";
            return false;
        }
        return true;
    }

    /// <summary>The copy stage's local size, as <c>cx_readback_c.glsl</c> declares it.</summary>
    private const int CopyCheckGroup = 8;
    /// <summary>The fixture's size: a 4x4 source, copied as a 2x2 block and blitted as a 2:1 reduction.</summary>
    private const int CopyCheckSize = 4;

    /// <summary>Both copies on this device, once per device, against a hand-made fixture. Oracle: a 4x4 R32F ramp of
    /// 1..16 written by hand, and the destination texels worked out here for a 2x2 block copy at an offset and for a
    /// 1:2 NEAREST blit whose destination centres all land inside a source texel -- never on its boundary, where
    /// nearest is the driver's choice.</summary>
    internal void CopyDeviceCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkBackend.CopyDeviceCheck");
        const int n = CopyCheckSize, half = n / 2;
        var ramp = new float[n * n];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = i + 1;
        ImageId src = Fixture(n, n, "copy check source"), dst = Fixture(n, n, "copy check destination"),
                big = Fixture(n, n, "copy check blit target");
        BufferId outBuf = CreateBuffer(new BufferDesc(ramp.Length * sizeof(uint), BufferUse.Storage | BufferUse.CopySrc,
                                                      MemoryKind.Device, "copy check read-back"));
        PipelineId copier = CopyChecker(out ShaderId reader);
        bool made = !src.IsNull && !dst.IsNull && !big.IsNull && !outBuf.IsNull && !copier.IsNull;
        check.Expect(made, "the fixture's three images, read-back buffer and copy stage exist");
        if (made)
        {
            var cl = new CommandList("vk copy check", 4096, log);
            // a fresh image holds undefined texels, so "untouched" below needs a known zero under the copy
            bool up = Upload(src, 0, 0, MemoryMarshal.AsBytes(ramp.AsSpan()))
                   && Upload(dst, 0, 0, MemoryMarshal.AsBytes(new float[ramp.Length].AsSpan()));
            check.Expect(up, "the 4x4 ramp uploads into the source and the destination is zeroed");
            // the source's bottom-right 2x2 -- 11, 12 over 15, 16 -- into the destination's top-left
            cl.CopyImage(new CopyImageCmd(src, new ImageRegion(0, half, half, 0), dst, new ImageRegion(0, 0, 0, 0), half, half, 1));
            var got = new float[ramp.Length];
            check.Expect(Submit(cl) && Read(cl, copier, dst, outBuf, n, n, got),
                "the copy submits and the destination reads back");
            check.Expect(got[0] == 11f && got[1] == 12f && got[n] == 15f && got[n + 1] == 16f,
                $"the copied 2x2 lands as 11, 12 over 15, 16 (got {got[0]}, {got[1]}, {got[n]}, {got[n + 1]})");
            check.Expect(got[half] == 0f && got[n * half] == 0f,
                $"...and nothing outside the block is touched (got {got[half]}, {got[n * half]})");

            cl.Reset();
            // the source's top-left 2x2 -- 1, 2 over 5, 6 -- stretched over the whole 4x4: each destination centre
            // sits at a quarter or three quarters of a source texel, so NEAREST doubles every one
            cl.BlitImage(new BlitImageCmd(src, new Rect(0, 0, half, half), big, new Rect(0, 0, n, n), Filter.Nearest));
            var grown = new float[ramp.Length];
            check.Expect(Submit(cl) && Read(cl, copier, big, outBuf, n, n, grown),
                "the blit submits and its target reads back");
            check.Expect(grown[0] == 1f && grown[1] == 1f && grown[2] == 2f && grown[3] == 2f,
                $"the 1:2 blit's first row is 1, 1, 2, 2 (got {grown[0]}, {grown[1]}, {grown[2]}, {grown[3]})");
            check.Expect(grown[n] == 1f && grown[n * 2] == 5f && grown[n * 3 + 3] == 6f,
                $"...its second row repeats the first, and its lower half doubles 5 and 6 (got {grown[n]}, {grown[n * 2]}, {grown[n * 3 + 3]})");
        }
        Destroy(src); Destroy(dst); Destroy(big); Destroy(outBuf); Destroy(copier); Destroy(reader);
        log.Info(check.Ok ? "VkBackend.CopyDeviceCheck: passed" : "VkBackend.CopyDeviceCheck: FAILED -- see above");
    }

    private ImageId Fixture(int w, int h, string label)
        => CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, w, h, 1, 1, 1, 1,
                                     ImageUse.Sampled | ImageUse.CopySrc | ImageUse.CopyDst, label));

    /// <summary>The one-word float copy stage of <c>cx_readback_c.glsl</c>.</summary>
    private PipelineId CopyChecker(out ShaderId cs)
    {
        const string Version = "#version 460 core\n";
        string src = CX.Alloy.ShaderSource.Get("cx_readback_c.glsl").Replace(Version, Version + "#define CX_READBACK_WORDS 1\n");
        cs = CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, "vk copy check reader"));
        var d = new PipelineDesc { Cs = cs, Label = "vk copy check reader" };
        return CreatePipeline(in d);
    }

    /// <summary><paramref name="img"/>'s texels through the copy stage into <paramref name="buf"/>, submitted and
    /// read back as floats.</summary>
    private bool Read(CommandList cl, PipelineId copier, ImageId img, BufferId buf, int w, int h, float[] into)
    {
        cl.Reset();
        cl.SetPipeline(copier);
        cl.BindSet(0, new BindSet(default, [new BufferBinding(CX.Alloy.SsboSlots.ReadbackOut, buf)], [new ImageBinding(0, img)], default));
        cl.Dispatch((w + CopyCheckGroup - 1) / CopyCheckGroup, (h + CopyCheckGroup - 1) / CopyCheckGroup);
        return Submit(cl) && ReadBack(buf, MemoryMarshal.AsBytes(into.AsSpan(0, w * h)));
    }
}

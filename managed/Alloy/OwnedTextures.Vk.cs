using System;
using CX.Engine;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Each upload site's bytes into <see cref="VkTextures"/>, beside the GL upload and from the same pointer:
/// no texel is read back from GL. Every method is a no-op unless the mirror has a device.</summary>
internal static unsafe partial class OwnedTextures
{
    private static SamplerDesc VkSampler(in Sampler s) => VkTextures.Sampler(s.Min, s.Mag, s.WrapS, s.WrapT, s.Aniso);

    /// <summary>An uncompressed cell, tightly packed into RGBA8 (or grey for one channel) and mipped on the device as
    /// GL mips it. A null image goes up zeroed, as GL's storage is.</summary>
    private static void MirrorRaw(uint dst, byte* img, int w, int cx, int cy, int cw, int ch, int bpp, in Sampler s, bool mip)
    {
        if (VkTextures.Device(_log) is not VkBackend vk) return;
        bool grey = bpp == 1;
        int levels = mip ? EngineLevels(cw, ch) : 1;
        ImageId id = VkTextures.Create(vk, dst, grey ? Format.R8 : Format.Rgba8, cw, ch, levels, grey, VkSampler(s), $"owned {dst}");
        if (id.IsNull) return;
        int outBpp = grey ? 1 : 4;
        byte[] px = new byte[cw * ch * outBpp];
        if (img != null)
        {
            var sheet = new ReadOnlySpan<byte>(img, checked(w * (cy + ch) * bpp));
            for (int y = 0; y < ch; y++)
            {
                int at = ((cy + y) * w + cx) * bpp, o = y * cw * outBpp;
                for (int x = 0; x < cw; x++, o += outBpp, at += bpp)
                    switch (bpp)
                    {
                        case 1: px[o] = sheet[at]; break;
                        case 3: px[o] = sheet[at]; px[o + 1] = sheet[at + 1]; px[o + 2] = sheet[at + 2]; px[o + 3] = 255; break;
                        case 4: px[o] = sheet[at]; px[o + 1] = sheet[at + 1]; px[o + 2] = sheet[at + 2]; px[o + 3] = sheet[at + 3]; break;
                        default:
                            // 4_4_4_4: one native-endian ushort, R in the high nibble down to A in the low
                            int v = sheet[at] | sheet[at + 1] << 8;
                            px[o] = (byte)((v >> 12) * 17); px[o + 1] = (byte)(((v >> 8) & 15) * 17);
                            px[o + 2] = (byte)(((v >> 4) & 15) * 17); px[o + 3] = (byte)((v & 15) * 17);
                            break;
                    }
            }
        }
        if (!vk.Upload(id, 0, 0, px)) { VkTextures.Forget(dst); return; }
        if (levels > 1) vk.GenerateMips(id);
    }

    /// <summary>A compressed chain the engine names, level by level from the texture's own runs.</summary>
    private static void MirrorCompressed(uint dst, CAurTexture t, uint glFmt, int w, int h, int levels, in Sampler s, bool red)
    {
        if (VkTextures.Device(_log) is not VkBackend vk) return;
        Format f = VkTextures.Compressed(glFmt);
        if (f == Format.Unknown) { VkTextures.Refuse($"owned {dst}: GL format 0x{glFmt:x}"); return; }
        ImageId id = VkTextures.Create(vk, dst, f, w, h, levels, red, VkSampler(s), $"owned {dst}");
        if (id.IsNull) return;
        for (int l = 0; l < levels; l++)
        {
            byte* level = t.CompressedLevel(l, out _, out _, out int size);
            if (!vk.Upload(id, l, 0, new ReadOnlySpan<byte>(level, size))) { VkTextures.Forget(dst); return; }
        }
    }

    /// <summary>A DDS chain under its real format, the padded levels walked as <see cref="UploadDdsFormat"/> walks them.</summary>
    private static void MirrorDds(uint dst, byte* chain, in DdsReader.Info i, int levels, in Sampler s, bool red)
    {
        if (VkTextures.Device(_log) is not VkBackend vk) return;
        Format f = VkTextures.Compressed(DdsReader.GlFormat(i.Format));
        if (f == Format.Unknown) { VkTextures.Refuse($"owned {dst}: DDS {i.Format}"); return; }
        ImageId id = VkTextures.Create(vk, dst, f, i.W, i.H, levels, red, VkSampler(s), $"owned {dst}");
        if (id.IsNull) return;
        var all = new ReadOnlySpan<byte>(chain, (int)i.ChainBytes);
        int off = 0;
        for (int l = 0, w = i.W, h = i.H; l < levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
        {
            int sz = DdsReader.LevelSize(w, h, i.Channels);
            if (!vk.Upload(id, l, 0, all.Slice(off, sz))) { VkTextures.Forget(dst); return; }
            off += sz;
        }
    }

    /// <summary>One RGBA8 texel, or zeroed RGBA8 storage of a cell's size: the stand-ins and the empty upload.</summary>
    private static void MirrorSolid(uint dst, int w, int h, uint rgba, in SamplerDesc s)
    {
        if (VkTextures.Device(_log) is not VkBackend vk) return;
        ImageId id = VkTextures.Create(vk, dst, Format.Rgba8, w, h, 1, false, s, $"owned {dst}");
        if (id.IsNull) return;
        byte[] px = new byte[w * h * 4];
        if (rgba != 0) for (int k = 0; k < px.Length; k += 4) { px[k] = (byte)rgba; px[k + 1] = (byte)(rgba >> 8); px[k + 2] = (byte)(rgba >> 16); px[k + 3] = (byte)(rgba >> 24); }
        if (!vk.Upload(id, 0, 0, px)) VkTextures.Forget(dst);
    }

    /// <summary>The nearest-filtered sampler the stand-ins carry.</summary>
    private static SamplerDesc NearestSampler => new(Filter.Nearest, Filter.Nearest, MipMode.None, Wrap.Repeat);

    /// <summary>The BCn re-encode of <paramref name="src"/>: its levels under <paramref name="dst"/>, with the sampler
    /// the raw upload's mirror carried (copied as GL copies it).</summary>
    private static void MirrorTranscode(uint src, uint dst, bool bc3, int levels, int[] lw, int[] lh, long[] off, byte[] blocks)
    {
        if (VkTextures.Device(_log) is not VkBackend vk || VkTextures.SamplerOf(src) is not SamplerDesc s) return;
        ImageId id = VkTextures.Create(vk, dst, bc3 ? Format.Bc3 : Format.Bc1, lw[0], lh[0], levels, false, s, $"owned {dst}");
        if (id.IsNull) return;
        for (int l = 0; l < levels; l++)
            if (!vk.Upload(id, l, 0, blocks.AsSpan((int)off[l], BcnEncode.LevelSize(lw[l], lh[l], bc3)))) { VkTextures.Forget(dst); return; }
    }
}

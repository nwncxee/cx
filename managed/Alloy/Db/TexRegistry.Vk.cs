using System;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy.Db;

/// <summary>The registry's uploads into <see cref="VkTextures"/>, beside GL's and from the same decoded job: every level
/// a scene part's texture reaches GL with, under the sampler GL gives it. No texel is read back from GL.</summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>A job's sampler as <see cref="Upload"/> sets it on GL: a PLT is always nearest and mipped.</summary>
    private static SamplerDesc VkSampler(bool clamp, bool nearest, bool mip)
    {
        Filter f = nearest ? Filter.Nearest : Filter.Linear;
        return new SamplerDesc(f, f, !mip ? MipMode.None : nearest ? MipMode.Nearest : MipMode.Linear,
                               clamp ? Wrap.Clamp : Wrap.Repeat, Aniso: mip && !nearest ? Aniso() : 1);
    }

    /// <summary><paramref name="j"/>'s texels under GL name <paramref name="dst"/>, as <see cref="Upload"/> just sent
    /// them: the compressed chain, the PLT's index chain, or the raw cell mipped on the device.</summary>
    private static void MirrorJob(uint dst, Job j)
    {
        if (VkTextures.Device(_log) is not VkBackend vk) return;
        bool plt = j.Kind == TexKind.Plt;
        bool nearest = plt || j.Nearest, mip = plt || j.Mip;
        SamplerDesc s = VkSampler(j.Clamp, nearest, mip);
        string label = $"tex {j.Resref}";
        if (j.Compressed)
        {
            Format f = VkTextures.Compressed(j.GlFmt);
            if (f == Format.Unknown) { VkTextures.Refuse($"{label}: GL format 0x{j.GlFmt:x}"); return; }
            ImageId id = VkTextures.Create(vk, dst, f, j.W, j.H, j.Levels, j.Channels == 1, s, label);
            if (id.IsNull) return;
            long off = 0;
            for (int l = 0, w = j.W, h = j.H; l < j.Levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                int sz = DdsReader.LevelSize(w, h, j.Channels);
                if (!vk.Upload(id, l, 0, new ReadOnlySpan<byte>(j.Data, checked((int)(off + sz))).Slice((int)off))) { VkTextures.Forget(dst); return; }
                off += sz;
            }
            return;
        }
        if (plt)
        {
            ImageId id = VkTextures.Create(vk, dst, Format.Rg8, j.W, j.H, j.Levels, false, s, label);
            if (id.IsNull) return;
            long off = 0;
            for (int l = 0, w = j.W, h = j.H; l < j.Levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                int sz = w * h * PltImage.BytesPerPixel;
                if (!vk.Upload(id, l, 0, new ReadOnlySpan<byte>(j.Data, checked((int)(off + sz))).Slice((int)off))) { VkTextures.Forget(dst); return; }
                off += sz;
            }
            return;
        }
        byte[] px = VkTextures.Pack(j.Data, j.FullW > 0 ? j.FullW : j.W, 0, 0, j.W, j.H, j.Channels, nibbles: false, out Format pf);
        int levels = j.GenMips ? j.Levels : 1;
        ImageId raw = VkTextures.Create(vk, dst, pf, j.W, j.H, levels, j.Channels == 1, s, label);
        if (raw.IsNull) return;
        if (!vk.Upload(raw, 0, 0, px)) { VkTextures.Forget(dst); return; }
        if (levels > 1) vk.GenerateMips(raw);
    }
}

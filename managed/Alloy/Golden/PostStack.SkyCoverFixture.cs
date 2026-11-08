using System;
using System.Runtime.InteropServices;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The FSR sky golden's fixture: a sky coverage history of the golden's own, so a run can start from garbage.</summary>
internal static unsafe partial class PostStack
{
    /// <summary>Seat two <paramref name="w"/> x <paramref name="h"/> coverage images holding <paramref name="fill"/> as
    /// the FSR sky coverage on <paramref name="vk"/>, marked as holding no history; false when refused.</summary>
    internal static bool SkyCoverFixture(VkBackend vk, int w, int h, Half fill)
    {
        if (_vk.Dev != vk) _vk = new StackVkState { Dev = vk };
        StackVkState s = _vk;
        vk.Destroy(s.Cover0); vk.Destroy(s.Cover1);
        var px = new Half[w * h];
        Array.Fill(px, fill);
        s.Cover0 = PostChainVk.Target(vk, w, h, Format.R16F, "golden sky coverage a", ImageUse.CopyDst);
        s.Cover1 = PostChainVk.Target(vk, w, h, Format.R16F, "golden sky coverage b", ImageUse.CopyDst);
        s.CoverW = w; s.CoverH = h; s.CoverValid = false;
        return !s.Cover0.IsNull && !s.Cover1.IsNull && vk.Upload(s.Cover0, 0, 0, MemoryMarshal.AsBytes(px.AsSpan()))
               && vk.Upload(s.Cover1, 0, 0, MemoryMarshal.AsBytes(px.AsSpan()));
    }
}

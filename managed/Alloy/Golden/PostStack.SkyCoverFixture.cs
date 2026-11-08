using System;
using System.Runtime.InteropServices;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The FSR sky golden's fixture: a sky coverage history of the golden's own, so a run can start from garbage.</summary>
internal static unsafe partial class PostStack
{
    /// <summary>Seat a <paramref name="w"/> x <paramref name="h"/> coverage pair holding <paramref name="fill"/> as the FSR
    /// sky coverage on <paramref name="vk"/>, marked as holding no history; false when refused.</summary>
    internal static bool SkyCoverFixture(VkBackend vk, int w, int h, Half fill)
    {
        if (_vk.Dev != vk) _vk = new StackVkState { Dev = vk };
        StackVkState s = _vk;
        SkyCoverForget(vk);
        var px = new Half[w * h];
        Array.Fill(px, fill);
        return CoverPair(vk, s, w, h, ImageUse.CopyDst) && vk.Upload(s.Cover0, 0, 0, MemoryMarshal.AsBytes(px.AsSpan()))
               && vk.Upload(s.Cover1, 0, 0, MemoryMarshal.AsBytes(px.AsSpan()));
    }

    /// <summary>Destroy the FSR sky coverage pair on <paramref name="vk"/>, so a golden leaves none behind.</summary>
    internal static void SkyCoverForget(VkBackend vk)
    {
        if (_vk.Dev != vk) return;
        vk.Destroy(_vk.Cover0); vk.Destroy(_vk.Cover1);
        _vk.Cover0 = _vk.Cover1 = default;
        _vk.CoverW = _vk.CoverH = 0;
        _vk.CoverValid = false;
    }
}

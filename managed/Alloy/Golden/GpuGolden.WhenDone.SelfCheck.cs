using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>A list's <see cref="CommandList.WhenDone"/> work runs once, in the order its lists ran, after the GPU finished its
    /// copy, and a list reset unsubmitted tells its work it never ran. Oracle: the hand-written texel and arguments.</summary>
    private static void WhenDoneSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 1, 1, 1, 1, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden whendone texel"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(4, BufferUse.CopyDst, MemoryKind.Readback, "golden whendone read"));
        try
        {
            check.Expect(!img.IsNull && !rb.IsNull, "whendone: the texel or its readback was refused (logged above)");
            if (img.IsNull || rb.IsNull) return;
            byte[] texel = [12, 34, 56, 78];
            var got = new List<(int Arg, bool Ran, byte[] Px)>();
            Action<int, bool> landed = (arg, ran) =>
            {
                var px = new byte[4];
                if (ran) vk.ReadMapped(rb, 0, px);
                got.Add((arg, ran, px));
            };
            var copy = new CommandList("golden whendone copy", 1 << 10, log);
            var later = new CommandList("golden whendone later", 1 << 10, log);
            check.Expect(vk.Upload(img, 0, 0, texel), "whendone: the texel's fill was refused");
            copy.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(0, 0, 0, 0), 1, 1, rb, 0));
            copy.WhenDone(landed, 7);
            later.WhenDone(landed, 8);
            check.Expect(vk.Submit(copy) && vk.Submit(later) && vk.WaitSubmitted(), "whendone: a submit or the wait was refused");
            check.Expect(vk.Submit(new CommandList("golden whendone tick", 256, log)), "whendone: the tick's submit was refused");
            check.Expect(got.Count == 2 && got[0].Arg == 7 && got[1].Arg == 8 && got[0].Ran && got[1].Ran,
                $"whendone: {got.Count} call(s) after the GPU finished, wanted 7 then 8, both ran");
            check.Expect(got.Count > 0 && got[0].Px.AsSpan().SequenceEqual(texel),
                "whendone: the copy's texel was not in the buffer when its work ran");
            check.Expect(vk.Submit(new CommandList("golden whendone tick 2", 256, log)) && got.Count == 2,
                "whendone: work ran again on a later submit");
            var dropped = new CommandList("golden whendone dropped", 256, log);
            dropped.WhenDone(landed, 9);
            dropped.Reset();
            check.Expect(got.Count == 3 && got[2].Arg == 9 && !got[2].Ran, "whendone: a list reset unsubmitted did not say its work never ran");
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(img);
        }
    }
}

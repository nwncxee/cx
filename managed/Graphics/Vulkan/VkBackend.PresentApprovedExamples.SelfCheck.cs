using System;
using CX.Framework;
using CX.Rhi;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Approved-spec: E3 and E5, a list held for the present on a device with no window. Oracle: a
    /// hand-written texel copied into a cleared composite by the held list, read back by a later list.</summary>
    internal void PresentDeviceCheck(Checker check, ExtenderLog log)
    {
        byte[] texel = [12, 34, 56, 78];
        ImageId px = CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 1, 1, 1, 1, 1, 1,
                                               ImageUse.Sampled | ImageUse.CopySrc | ImageUse.CopyDst, "present check texel"));
        BufferId rb = CreateBuffer(new BufferDesc(4, BufferUse.CopyDst, MemoryKind.Readback, "present check read"));
        bool made = !px.IsNull && !rb.IsNull && Resize(1, 1) && Upload(px, 0, 0, texel);
        check.Expect(made, "present check: the texel, its readback and a 1x1 composite exist");
        if (!made) { Destroy(px); Destroy(rb); return; }
        var held = new CommandList("present check held", 256, log);
        var other = new CommandList("present check reader", 256, log);

        // Given a GUI list held for the present
        // When another list is submitted before the present
        // Then the held list runs first, and the other list sees what it drew
        byte[] got = HeldThenRead(held, other, texel, present: false, log);
        check.Expect(got.AsSpan().SequenceEqual(texel),
            $"E3: a list submitted before the present reads the held list's texel 12,34,56,78 (got {string.Join(",", got)})");

        // Given a GUI list held for the present
        // When the window refuses the frame, minimised or with no surface
        // Then the held list still runs, and a later readback of the composite shows what it drew
        got = HeldThenRead(held, other, texel, present: true, log);
        check.Expect(got.AsSpan().SequenceEqual(texel),
            $"E5: after a refused present the composite holds the held list's texel 12,34,56,78 (got {string.Join(",", got)})");

        Destroy(px); Destroy(rb);

        byte[] HeldThenRead(CommandList h, CommandList o, byte[] want, bool present, ExtenderLog l)
        {
            o.Reset();
            o.ClearImage(_composite);
            if (!Submit(o)) return [];
            h.Reset();
            h.CopyImage(new CopyImageCmd(px, new ImageRegion(0, 0, 0, 0), _composite, new ImageRegion(0, 0, 0, 0), 1, 1, 1));
            if (!HoldForPresent(h)) return [];
            if (present) Present(1, l);
            o.Reset();
            o.CopyImageToBuffer(new CopyToBufferCmd(_composite, new ImageRegion(0, 0, 0, 0), 1, 1, rb, 0));
            var back = new byte[want.Length];
            return Submit(o) && WaitSubmitted() && ReadMapped(rb, 0, back) ? back : [];
        }
    }
}

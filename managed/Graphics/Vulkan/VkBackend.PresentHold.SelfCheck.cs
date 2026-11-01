using System;
using CX.Framework;
using CX.Rhi;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>A held command buffer's edges: a wait with no submit after the hold, an upload queued while one is held,
    /// and an upload at a held buffer's head that must not run again after it. Oracle: hand-written texels read back
    /// from a 1x1 composite.</summary>
    internal void PresentHoldCheck(Checker check, ExtenderLog log)
    {
        byte[] a = [1, 2, 3, 4], c = [90, 80, 70, 60], d = [5, 6, 7, 8];
        ImageId pa = Texel("present hold texel a"), pc = Texel("present hold texel c"), pd = Texel("present hold texel d");
        BufferId rb = CreateBuffer(new BufferDesc(4, BufferUse.CopyDst, MemoryKind.Readback, "present hold read"));
        var h = new CommandList("present hold held", 256, log);
        var o = new CommandList("present hold reader", 256, log);
        bool made = !pa.IsNull && !pc.IsNull && !pd.IsNull && !rb.IsNull && Resize(1, 1) && Upload(pa, 0, 0, a) && Upload(pc, 0, 0, c);
        o.ClearImage(_composite);
        check.Expect(made && Submit(o), "present hold: three texels, a readback and a cleared 1x1 composite exist");
        if (made)
        {
            h.Reset();
            h.CopyImage(Copy(pa, _composite));
            h.CopyImageToBuffer(new CopyToBufferCmd(_composite, new ImageRegion(0, 0, 0, 0), 1, 1, rb, 0));
            var got = new byte[4];
            bool read = HoldForPresent(h) && WaitSubmitted() && ReadMapped(rb, 0, got);
            check.Expect(read && got.AsSpan().SequenceEqual(a),
                $"a wait after a hold, with no submit between, waits out the held buffer too: 1,2,3,4 (got {string.Join(",", got)})");

            h.Reset();
            h.ClearImage(_composite);
            bool held = HoldForPresent(h) && Upload(pd, 0, 0, d);
            got = Read(o, pd, rb, held);
            check.Expect(got.AsSpan().SequenceEqual(d),
                $"an upload queued while a buffer is held lands at the next submit: 5,6,7,8 (got {string.Join(",", got)})");

            held = Upload(pa, 0, 0, a);
            h.Reset();
            h.CopyImage(Copy(pc, pa));
            held = held && HoldForPresent(h);
            got = Read(o, pa, rb, held);
            check.Expect(got.AsSpan().SequenceEqual(c),
                $"an upload at a held buffer's head runs once, not again over what the buffer drew: 90,80,70,60 (got {string.Join(",", got)})");

            h.Reset();
            h.CopyImage(Copy(pd, _composite));
            held = HoldForPresent(h);
            o.Reset();
            o.CopyImageToBuffer(new CopyToBufferCmd(_composite, new ImageRegion(0, 0, 0, 0), 1, 1, rb, 0));
            got = new byte[4];
            read = held && HoldForPresent(o) && WaitSubmitted() && ReadMapped(rb, 0, got);
            check.Expect(read && got.AsSpan().SequenceEqual(d),
                $"a second hold queues the first ahead of it: 5,6,7,8 (got {string.Join(",", got)})");

            BufferId dev = CreateBuffer(new BufferDesc(4, BufferUse.Storage | BufferUse.CopySrc | BufferUse.CopyDst, MemoryKind.Device, "present hold device"));
            h.Reset();
            h.UpdateBuffer(dev, 0, c);
            got = new byte[4];
            read = !dev.IsNull && HoldForPresent(h) && ReadBack(dev, got);
            check.Expect(read && got.AsSpan().SequenceEqual(c),
                $"a one-shot readback after a hold sees what the held buffer wrote: 90,80,70,60 (got {string.Join(",", got)})");
            Destroy(dev);

            int told = 0;
            h.Reset();
            h.ClearImage(_composite);
            h.WhenDone((_, ran) => told = ran ? 1 : -1, 0);
            o.Reset();
            bool queued = HoldForPresent(h) && Submit(o) && WaitSubmitted() && Submit(o);
            check.Expect(queued && told == 1, $"a held buffer's WhenDone work is told it ran, once it has (got {told})");
        }
        Destroy(pa); Destroy(pc); Destroy(pd); Destroy(rb);

        ImageId Texel(string label) => CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 1, 1, 1, 1, 1, 1,
                                                                 ImageUse.Sampled | ImageUse.CopySrc | ImageUse.CopyDst, label));
        static CopyImageCmd Copy(ImageId from, ImageId to)
            => new(from, new ImageRegion(0, 0, 0, 0), to, new ImageRegion(0, 0, 0, 0), 1, 1, 1);
        byte[] Read(CommandList l, ImageId img, BufferId buf, bool ok)
        {
            l.Reset();
            l.CopyImage(Copy(img, _composite));
            l.CopyImageToBuffer(new CopyToBufferCmd(_composite, new ImageRegion(0, 0, 0, 0), 1, 1, buf, 0));
            var back = new byte[4];
            return ok && Submit(l) && WaitSubmitted() && ReadMapped(buf, 0, back) ? back : [];
        }
    }
}

using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>A new texture's base filled, its mips asked for, then mip 1 filled by hand before any submit: the hand
    /// fill lands after the chain, so mip 1 reads it. Oracle: the hand-written mip 1 texel.</summary>
    private static void TransferAfterMips(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 2, 2, 1, 2, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden transfer mips then fill"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(4, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden transfer mip read"));
        try
        {
            check.Expect(!img.IsNull && !rb.IsNull, "transfer order: the texture or its readback was refused (logged above)");
            if (img.IsNull || rb.IsNull) return;
            byte[] mip1 = [9, 99, 199, 249];
            bool filled = vk.Upload(img, 0, 0, new byte[16]);
            vk.GenerateMips(img);
            filled &= vk.Upload(img, 1, 0, mip1);
            var cl = new CommandList("golden transfer order readback", 1 << 10, log);
            cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(1, 0, 0, 0), 1, 1, rb, 0));
            var got = new byte[4];
            check.Expect(filled && vk.Submit(cl) && vk.ReadBack(rb, got), "transfer order: a fill or the readback was refused");
            check.Expect(got.AsSpan().SequenceEqual(mip1), $"transfer order: mip 1 reads {got[0]},{got[1]},{got[2]},{got[3]}, not the fill made after the chain");
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(img);
        }
    }

    /// <summary>Two fills of one new buffer's same bytes before any submit land in the order made: the read gives the
    /// second. Oracle: the hand-written patterns.</summary>
    private static void TransferRefill(VkBackend vk, Checker check)
    {
        BufferId buf = vk.CreateBuffer(new BufferDesc(64, BufferUse.Vertex | BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden transfer refill"));
        try
        {
            check.Expect(!buf.IsNull, "transfer refill: the buffer was refused (logged above)");
            if (buf.IsNull) return;
            byte[] first = TransferBytes(64, 11), second = TransferBytes(64, 150), got = new byte[64];
            bool ran = vk.Upload(buf, 0, first) && vk.Upload(buf, 0, second)
                    && vk.Submit(new CommandList("golden transfer frame")) && vk.ReadBack(buf, got);
            check.Expect(ran, "transfer refill: a fill, the frame or the read was refused");
            check.Expect(got.AsSpan().SequenceEqual(second), "transfer refill: the buffer does not read back the later of two fills");
        }
        finally { vk.Destroy(buf); }
    }
}

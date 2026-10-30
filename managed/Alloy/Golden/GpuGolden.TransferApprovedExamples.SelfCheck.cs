using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The transfer golden (approved-spec): a resource's first fill goes out on the transfer queue and arrives whole; a
/// fill into one a frame already used rides the frame's own submit and arrives as well. Oracles: hand-written byte
/// patterns, and a 2x2 texture whose mip 1 average is worked by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>A fixed byte pattern, distinct per <paramref name="seed"/>, the uploads carry.</summary>
    private static byte[] TransferBytes(int n, int seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 7 + seed);
        return b;
    }

    private static void TransferSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        if (!vk.HasTransfer) check.Skip("no copy-only queue family on this device: E1, E2 and E3 check what arrives, not the queue");
        TransferImage(vk, check, log);
        TransferBuffer(vk, check);
        TransferMips(vk, check, log);
    }

    /// <summary>E1 and E1b: a new 4x4 texture filled, read back; filled again after that frame used it, read back.</summary>
    private static void TransferImage(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId img = PostImage(vk, Format.Rgba8, 4, 4, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden transfer texture");
        BufferId rb = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst, MemoryKind.Device, "golden transfer read"));
        try
        {
            check.Expect(!img.IsNull && !rb.IsNull, "E1: the texture or its readback was refused (logged above)");
            if (img.IsNull || rb.IsNull) return;
            byte[] first = TransferBytes(64, 3), second = TransferBytes(64, 101);
            long before = vk.Shipped;
            check.Expect(vk.Upload(img, 0, 0, first), "E1: the first fill was refused (logged above)");
            byte[]? got = PostRead(vk, log, img, 4, 4, 4, rb);
            check.Expect(got != null && got.AsSpan().SequenceEqual(first), "E1: the new texture does not read back the uploaded texels");
            check.Expect(!vk.HasTransfer || vk.Shipped == before + 1, $"E1: the new texture's fill sent {vk.Shipped - before} copies on the transfer queue, not 1");
            before = vk.Shipped;
            check.Expect(vk.Upload(img, 0, 0, second), "E1b: the second fill was refused (logged above)");
            got = PostRead(vk, log, img, 4, 4, 4, rb);
            check.Expect(got != null && got.AsSpan().SequenceEqual(second), "E1b: the used texture does not read back the new texels");
            check.Expect(!vk.HasTransfer || vk.Shipped == before, "E1b: a fill into a texture a frame already used went on the transfer queue");
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(img);
        }
    }

    /// <summary>E2 and E2b: a new vertex buffer filled, read back; a frame writes its head, more bytes fill its tail.</summary>
    private static void TransferBuffer(VkBackend vk, Checker check)
    {
        BufferId buf = vk.CreateBuffer(new BufferDesc(256, BufferUse.Vertex | BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden transfer vertices"));
        try
        {
            check.Expect(!buf.IsNull, "E2: the vertex buffer was refused (logged above)");
            if (buf.IsNull) return;
            byte[] first = TransferBytes(256, 5), head = TransferBytes(128, 77), tail = TransferBytes(128, 200);
            long before = vk.Shipped;
            var got = new byte[256];
            check.Expect(vk.Upload(buf, 0, first), "E2: the first fill was refused (logged above)");
            check.Expect(vk.Submit(new CommandList("golden transfer frame")) && vk.ReadBack(buf, got), "E2: the frame or the read was refused");
            check.Expect(got.AsSpan().SequenceEqual(first), "E2: the new vertex buffer does not read back the uploaded bytes");
            check.Expect(!vk.HasTransfer || vk.Shipped == before + 1, $"E2: the new buffer's fill sent {vk.Shipped - before} copies on the transfer queue, not 1");
            var cl = new CommandList("golden transfer use");
            cl.UpdateBuffer(buf, 0, head);
            check.Expect(vk.Submit(cl), "E2b: the frame that writes the buffer was refused");
            before = vk.Shipped;
            check.Expect(vk.Upload(buf, 128, tail), "E2b: the second fill was refused (logged above)");
            check.Expect(vk.Submit(new CommandList("golden transfer frame")) && vk.ReadBack(buf, got), "E2b: the frame or the read was refused");
            check.Expect(got.AsSpan(0, 128).SequenceEqual(head) && got.AsSpan(128).SequenceEqual(tail),
                "E2b: the used buffer does not read back the frame's head and the new tail");
            check.Expect(!vk.HasTransfer || vk.Shipped == before, "E2b: a fill into a buffer a frame already used went on the transfer queue");
        }
        finally { vk.Destroy(buf); }
    }

    /// <summary>E3: a new 2x2 texture's base filled, its mips generated; mip 1 is the average of the four texels.
    /// Oracle by hand: red 0/100/200/40 -> 85, green 10/20/30/40 -> 25, blue 255/255/1/1 -> 128, alpha 255.</summary>
    private static void TransferMips(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 2, 2, 1, 2, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden transfer mips"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(4, BufferUse.CopyDst, MemoryKind.Device, "golden transfer mip read"));
        try
        {
            check.Expect(!img.IsNull && !rb.IsNull, "E3: the texture or its readback was refused (logged above)");
            if (img.IsNull || rb.IsNull) return;
            long before = vk.Shipped;
            byte[] texels = [0, 10, 255, 255, 100, 20, 255, 255, 200, 30, 1, 255, 40, 40, 1, 255];
            check.Expect(vk.Upload(img, 0, 0, texels), "E3: the base fill was refused (logged above)");
            vk.GenerateMips(img);
            var cl = new CommandList("golden transfer mip readback", 1 << 10, log);
            cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(1, 0, 0, 0), 1, 1, rb, 0));
            var got = new byte[4];
            bool read = vk.Submit(cl) && vk.ReadBack(rb, got);
            check.Expect(read, "E3: the mip readback was refused");
            byte[] want = [85, 25, 128, 255];
            int worst = 0;
            for (int k = 0; k < 4; k++) worst = Math.Max(worst, Math.Abs(got[k] - want[k]));
            check.Expect(read && worst <= 1, $"E3: mip 1 reads {got[0]},{got[1]},{got[2]},{got[3]}, not the base's average 85,25,128,255");
            check.Expect(!vk.HasTransfer || vk.Shipped == before + 1, $"E3: the base fill sent {vk.Shipped - before} copies on the transfer queue, not 1");
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(img);
        }
    }
}

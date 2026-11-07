using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Rhi;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>A buffer write larger than the whole staging budget lands in full. Oracle: each word holds its own
    /// byte offset, read back on both sides of a staging block's edge and at the far end.</summary>
    internal void UploadApprovedExamples(Checker check)
    {
        const int Bytes = (int)StagingBudget + (44 << 20);
        var words = new int[Bytes / sizeof(int)];
        for (int i = 0; i < words.Length; i++) words[i] = i * sizeof(int);
        BufferId dev = CreateBuffer(new BufferDesc(Bytes, BufferUse.Vertex | BufferUse.CopySrc | BufferUse.CopyDst, MemoryKind.Device, "upload example"));
        BufferId rb = CreateBuffer(new BufferDesc(16, BufferUse.CopyDst, MemoryKind.Readback, "upload example read"));
        bool sent = !dev.IsNull && !rb.IsNull && Upload(dev, 0, MemoryMarshal.AsBytes(words.AsSpan()));
        check.Expect(sent, "a write past the staging budget is accepted");
        if (sent)
        {
            int edge = (int)UploadBlockBytes;
            int[] at = [0, edge - sizeof(int), edge, Bytes - sizeof(int)];
            var l = new CommandList("upload example", 256, _log);
            for (int k = 0; k < at.Length; k++) l.CopyBuffer(new CopyBufferCmd(dev, at[k], rb, k * sizeof(int), sizeof(int)));
            var got = new int[at.Length];
            bool read = Submit(l) && WaitSubmitted()
                     && ReadMapped(rb, 0, MemoryMarshal.AsBytes(got.AsSpan()));
            check.Expect(read && got.AsSpan().SequenceEqual(at),
                $"every word arrives where it was written: {string.Join(",", at)} (got {string.Join(",", got)})");
        }
        Destroy(dev);
        Destroy(rb);
    }
}

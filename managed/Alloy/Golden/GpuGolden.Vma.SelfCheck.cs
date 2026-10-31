using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The alias groups past the approved examples: two members used in one submit each read back their own colour, and
/// a group whose largest member goes moves onto the size the rest need. Oracles: hand-written clear colours and the
/// device's own size for a lone 4x4.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The alias goldens past the approved ones, on a settle buffer of their own.</summary>
    private static void VmaMoreSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        BufferId settle = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden vma more settle"));
        check.Expect(!settle.IsNull, "the alias goldens' settle buffer was refused (logged above)");
        if (settle.IsNull) return;
        AliasOneSubmit(vk, check, log);
        AliasShrink(vk, check, log, settle);
        vk.Destroy(settle);
    }

    /// <summary>Holds the ordering between two members inside one submit; turns red when the barrier a claim places
    /// stops waiting on the other member's work.</summary>
    private static void AliasOneSubmit(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId first = Target(vk, 4, "golden alias one-submit first", "golden one"), second = Target(vk, 4, "golden alias one-submit second", "golden one");
        BufferId ra = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden alias one-submit read a"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden alias one-submit read b"));
        try
        {
            check.Expect(!first.IsNull && !second.IsNull && !ra.IsNull && !rb.IsNull, "a one-submit alias target was refused (logged above)");
            if (first.IsNull || second.IsNull || ra.IsNull || rb.IsNull) return;
            var cl = new CommandList("golden alias one submit", 1 << 10, log);
            ReadOnlySpan<ColorAttachment> a = [new ColorAttachment(first, 0, 0, LoadOp.Clear, StoreOp.Store, new Vector4(1, 0, 0, 1))];
            cl.BeginRenderPass(new RenderPassDesc(a));
            cl.EndRenderPass();
            cl.CopyImageToBuffer(new CopyToBufferCmd(first, new ImageRegion(0, 0, 0, 0), 4, 4, ra, 0));
            ReadOnlySpan<ColorAttachment> b = [new ColorAttachment(second, 0, 0, LoadOp.Clear, StoreOp.Store, new Vector4(0, 0, 1, 1))];
            cl.BeginRenderPass(new RenderPassDesc(b));
            cl.EndRenderPass();
            cl.CopyImageToBuffer(new CopyToBufferCmd(second, new ImageRegion(0, 0, 0, 0), 4, 4, rb, 0));
            var got = new byte[64];
            check.Expect(vk.Submit(cl), "the one-submit alias list was refused (logged above)");
            check.Expect(vk.ReadBack(ra, got) && got.AsSpan().SequenceEqual(Texels(255, 0, 0, 255)), "inside one submit the first member does not read back its red");
            check.Expect(vk.ReadBack(rb, got) && got.AsSpan().SequenceEqual(Texels(0, 0, 255, 255)), "inside one submit the second member does not read back its blue");
        }
        finally
        {
            vk.Destroy(first); vk.Destroy(second); vk.Destroy(ra); vk.Destroy(rb);
        }
    }

    /// <summary>Holds a group giving memory back when its largest member goes; turns red when the shrink is skipped.</summary>
    private static void AliasShrink(VkBackend vk, Checker check, ExtenderLog log, BufferId settle)
    {
        Settle(vk, log, settle);
        (long Count, long Bytes) before = vk.Allocations;
        ImageId lone = Target(vk, 4, "golden shrink lone", null);
        long four = vk.Allocations.Bytes - before.Bytes;
        vk.Destroy(lone);
        Settle(vk, log, settle);
        before = vk.Allocations;
        ImageId small = Target(vk, 4, "golden shrink small", "golden shrink"), large = Target(vk, 64, "golden shrink large", "golden shrink");
        vk.Destroy(large);
        Settle(vk, log, settle);
        check.Expect(vk.Allocations.Bytes - before.Bytes == four,
                     $"a group whose 64x64 went holds {vk.Allocations.Bytes - before.Bytes} bytes, not the {four} its 4x4 needs");
        vk.Destroy(small);
        Settle(vk, log, settle);
    }
}

using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The memory goldens (approved-spec): render targets in one alias group share one allocation and each reads back
/// what was last written into it; uploads past the staging budget arrive whole and the staging shrinks back once
/// the loads stop; the largest allocations are named. Oracles: the device's own size for a lone image, hand-written
/// clear colours and byte patterns, and the budget and block sizes in MiB.
/// </summary>
internal static partial class GpuGolden
{
    private const long MiB = 1 << 20;

    /// <summary>Every memory golden, the largest-allocation one first, while nothing else large is held. One small
    /// buffer, made first and held throughout, carries every settle and readback, so no golden's count sees it come or go.</summary>
    private static void VmaSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        BufferId rb = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden vma read"));
        check.Expect(!rb.IsNull, "the memory goldens' readback was refused (logged above)");
        if (rb.IsNull) return;
        try
        {
            Settle(vk, log, rb);
            LargestNamed(vk, check, log, rb);
            AliasShare(vk, check, log, rb);
            AliasContents(vk, check, log, rb);
            AliasAsync(vk, check);
            StagingLoad(vk, log);
        }
        finally { vk.Destroy(rb); }
    }

    /// <summary>Submit and wait out enough frames that every free queued behind them has happened.</summary>
    private static void Settle(VkBackend vk, ExtenderLog log, BufferId rb)
    {
        Span<byte> four = stackalloc byte[4];
        for (int i = 0; i < 6; i++)
        {
            vk.Submit(new CommandList("golden settle", 1 << 8, log));
            vk.ReadBack(rb, four);
        }
    }

    private static ImageId Target(VkBackend vk, int size, string label, string? alias, ImageUse extra = ImageUse.None)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, size, size, 1, 1, 1, 1,
                                        ImageUse.ColorTarget | ImageUse.Sampled | ImageUse.CopySrc | extra, label, alias));

    /// <summary>S1 on the device: three buffers far larger than anything else held come back as the three largest, by name and size.</summary>
    private static void LargestNamed(VkBackend vk, Checker check, ExtenderLog log, BufferId rb)
    {
        BufferId a = vk.CreateBuffer(new BufferDesc(48 * MiB, BufferUse.Storage, MemoryKind.Device, "golden vram first"));
        BufferId b = vk.CreateBuffer(new BufferDesc(40 * MiB, BufferUse.Storage, MemoryKind.Device, "golden vram second"));
        BufferId c = vk.CreateBuffer(new BufferDesc(32 * MiB, BufferUse.Storage, MemoryKind.Device, "golden vram third"));
        try
        {
            (string Name, long Bytes)[] top = vk.Largest(3);
            check.Expect(top.Length == 3, $"S1: {top.Length} largest allocations named, not 3");
            if (top.Length != 3) return;
            check.Expect(top[0].Name == "golden vram first" && top[0].Bytes / MiB == 48, $"S1: the largest is {top[0].Name} at {top[0].Bytes / MiB}MiB, not golden vram first at 48MiB");
            check.Expect(top[1].Name == "golden vram second" && top[1].Bytes / MiB == 40, $"S1: the second is {top[1].Name} at {top[1].Bytes / MiB}MiB, not golden vram second at 40MiB");
            check.Expect(top[2].Name == "golden vram third" && top[2].Bytes / MiB == 32, $"S1: the third is {top[2].Name} at {top[2].Bytes / MiB}MiB, not golden vram third at 32MiB");
        }
        finally
        {
            vk.Destroy(a); vk.Destroy(b); vk.Destroy(c);
            Settle(vk, log, rb);
        }
    }

    /// <summary>A3: a 4x4 and an 8x8 in one group take one allocation the size of a lone 8x8; in two groups, two.</summary>
    private static void AliasShare(VkBackend vk, Checker check, ExtenderLog log, BufferId rb)
    {
        (long Count, long Bytes) before = vk.Allocations;
        ImageId lone = Target(vk, 8, "golden alias lone", null);
        long eight = vk.Allocations.Bytes - before.Bytes;
        vk.Destroy(lone);
        Settle(vk, log, rb);
        before = vk.Allocations;
        ImageId small = Target(vk, 4, "golden alias small", "golden lane"), large = Target(vk, 8, "golden alias large", "golden lane");
        Settle(vk, log, rb);
        (long Count, long Bytes) now = vk.Allocations;
        check.Expect(!small.IsNull && !large.IsNull, "A3: an aliased target was refused (logged above)");
        check.Expect(now.Count - before.Count == 1, $"A3: a 4x4 and an 8x8 in one group hold {now.Count - before.Count} allocations, not 1");
        check.Expect(now.Bytes - before.Bytes == eight, $"A3: the group holds {now.Bytes - before.Bytes} bytes, not the {eight} a lone 8x8 takes");
        vk.Destroy(small); vk.Destroy(large);
        Settle(vk, log, rb);
        before = vk.Allocations;
        ImageId p = Target(vk, 4, "golden alias apart small", "golden lane p"), q = Target(vk, 8, "golden alias apart large", "golden lane q");
        Settle(vk, log, rb);
        check.Expect(vk.Allocations.Count - before.Count == 2, $"A3: a 4x4 and an 8x8 in two groups hold {vk.Allocations.Count - before.Count} allocations, not 2");
        vk.Destroy(p); vk.Destroy(q);
        Settle(vk, log, rb);
    }

    /// <summary>Clear <paramref name="img"/> (4x4) to <paramref name="c"/> in a frame of its own, then read it back.</summary>
    private static byte[]? ClearRead(VkBackend vk, ExtenderLog log, ImageId img, Vector4 c, BufferId rb)
    {
        var cl = new CommandList("golden alias clear", 1 << 10, log);
        ReadOnlySpan<ColorAttachment> at = [new ColorAttachment(img, 0, 0, LoadOp.Clear, StoreOp.Store, c)];
        cl.BeginRenderPass(new RenderPassDesc(at));
        cl.EndRenderPass();
        return vk.Submit(cl) ? PostRead(vk, log, img, 4, 4, 4, rb) : null;
    }

    /// <summary>Clear <paramref name="img"/> (4x4) to <paramref name="c"/> inside <paramref name="cl"/>, then copy it
    /// into <paramref name="to"/>, so a write and its read sit in one recording.</summary>
    private static void ClearInto(CommandList cl, ImageId img, Vector4 c, BufferId to)
    {
        ReadOnlySpan<ColorAttachment> at = [new ColorAttachment(img, 0, 0, LoadOp.Clear, StoreOp.Store, c)];
        cl.BeginRenderPass(new RenderPassDesc(at));
        cl.EndRenderPass();
        cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(0, 0, 0, 0), 4, 4, to, 0));
    }

    /// <summary>A4: two targets of one group, written and read in turn inside ONE submit, read back their own colour;
    /// the next frame's use of only the first reads that frame's colour.</summary>
    private static void AliasContents(VkBackend vk, Checker check, ExtenderLog log, BufferId rb)
    {
        BufferId ra = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden alias first read"));
        BufferId rc = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden alias second read"));
        (long Count, long Bytes) before = vk.Allocations;
        ImageId first = Target(vk, 4, "golden alias first", "golden pair"), second = Target(vk, 4, "golden alias second", "golden pair");
        try
        {
            check.Expect(!first.IsNull && !second.IsNull && !ra.IsNull && !rc.IsNull, "A4: a target or its readback was refused (logged above)");
            if (first.IsNull || second.IsNull || ra.IsNull || rc.IsNull) return;
            Settle(vk, log, rb);
            check.Expect(vk.Allocations.Count - before.Count == 1, "A4: the two targets of one group do not share one allocation");
            var cl = new CommandList("golden alias one frame", 1 << 10, log);
            ClearInto(cl, first, new Vector4(1, 0, 0, 1), ra);
            ClearInto(cl, second, new Vector4(0, 0, 1, 1), rc);
            var got = new byte[64];
            check.Expect(vk.Submit(cl), "A4: the frame writing and reading both targets was refused (logged above)");
            check.Expect(vk.ReadBack(ra, got) && Solid(got, 255, 0, 0, 255), "A4: the first target does not read back the red written into it");
            check.Expect(vk.ReadBack(rc, got) && Solid(got, 0, 0, 255, 255), "A4: the second target does not read back the blue written into it");
            byte[]? next = ClearRead(vk, log, first, new Vector4(0, 1, 0, 1), rb);
            check.Expect(next != null && Solid(next, 0, 255, 0, 255), "A4: the next frame's first target does not read back the green written into it");
        }
        finally
        {
            vk.Destroy(first); vk.Destroy(second); vk.Destroy(ra); vk.Destroy(rc);
            Settle(vk, log, rb);
        }
    }

    /// <summary>A5: a target the async queue uses is refused an alias group, every attempt logging its own refusal;
    /// with no group it is made. Oracle: one counted refusal per attempt and the words the log must hold, by hand.</summary>
    private static void AliasAsync(VkBackend vk, Checker check)
    {
        int faults = vk.Faults;
        ImageId refused = Target(vk, 4, "golden alias async", "golden lane", ImageUse.Async);
        check.Expect(refused.IsNull, "A5: an async target was made in an alias group");
        check.Expect(vk.Faults == faults + 1, $"A5: the refusal logged {vk.Faults - faults} lines, not 1");
        check.Expect(vk.LastFault.Contains("golden alias async", StringComparison.Ordinal)
                     && vk.LastFault.Contains("queues", StringComparison.Ordinal),
                     $"A5: the refusal does not name the target and the queues as its reason: \"{vk.LastFault}\"");
        vk.Destroy(refused);
        ImageId again = Target(vk, 4, "golden alias async twice", "golden lane", ImageUse.Async);
        check.Expect(again.IsNull, "A5: a second async target was made in an alias group");
        check.Expect(vk.Faults == faults + 2, $"A5: the second refusal logged {vk.Faults - faults - 1} lines, not 1");
        vk.Destroy(again);
        ImageId made = Target(vk, 4, "golden alias async apart", null, ImageUse.Async);
        check.Expect(!made.IsNull, "A5: the same async target with no alias group was refused");
        check.Expect(vk.Faults == faults + 2, $"A5: making it apart logged {vk.Faults - faults - 2} refusals, not 0");
        vk.Destroy(made);
    }

    /// <summary>A byte pattern a texture's upload carries, distinct per <paramref name="seed"/>.</summary>
    private static void Pattern(byte[] b, int seed)
    {
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 13 + seed);
    }

    /// <summary>The staging goldens' texture side: 100 MB of RGBA8 each, well past one staging block.</summary>
    private const int StagingSide = 5000;

    /// <summary>L1 and L2: a lone 100 MB texture, then six more back to back in one frame, each read back whole while
    /// the staging held stays inside 256 MiB; it stays where the load left it ten frames on and is 64 MiB a thousand on.
    /// On its own suite, which skips on a device too small to hold the load. Oracle: hand-written byte patterns.</summary>
    private static void StagingLoad(VkBackend vk, ExtenderLog log)
    {
        const int Textures = 6;
        const long Budget = 256 * MiB, OneBlock = 64 * MiB;
        var check = new Checker(log, "GpuGolden.StagingLoad.SelfCheck");
        long want = (long)(Textures + 2) * StagingSide * StagingSide * 4 + Budget;
        vk.DeviceLocalBytes(out long usage, out long budget, out _, out _);
        if (budget - usage < want)
        {
            check.Skip($"the load needs {want / MiB}MiB of device-local memory -- the textures, the readback and the"
                       + $" staging budget -- and this device has {Math.Max(budget - usage, 0) / MiB}MiB free");
            return;
        }
        var texels = new byte[(long)StagingSide * StagingSide * 4];
        var got = new byte[texels.Length];
        BufferId rb = vk.CreateBuffer(new BufferDesc(texels.Length, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden staging read"));
        var imgs = new ImageId[Textures + 1];
        try
        {
            check.Expect(!rb.IsNull, "L1: the readback was refused (logged above)");
            if (rb.IsNull) return;
            for (int i = 0; i <= Textures; i++)
                imgs[i] = PostImage(vk, Format.Rgba8, StagingSide, StagingSide, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, $"golden staging {i}");
            Pattern(texels, 0);
            check.Expect(vk.Upload(imgs[0], 0, 0, texels), "L1: the lone 100 MB texture's upload was refused (logged above)");
            check.Expect(Arrives(vk, log, imgs[0], rb, texels, got), "L1: the lone 100 MB texture did not arrive whole");
            ulong high = 0;
            for (int i = 1; i <= Textures; i++)
            {
                Pattern(texels, i);
                check.Expect(vk.Upload(imgs[i], 0, 0, texels), $"L1: texture {i}'s upload was refused (logged above)");
                high = Math.Max(high, vk.Staging.Bytes);
            }
            check.Expect(high <= (ulong)Budget, $"L1: the staging held reached {high / MiB}MiB, past the 256MiB budget");
            for (int i = 1; i <= Textures; i++)
            {
                Pattern(texels, i);
                check.Expect(Arrives(vk, log, imgs[i], rb, texels, got), $"L1: texture {i} of the 600 MB load did not arrive whole");
            }
            ulong left = vk.Staging.Bytes;
            for (int f = 0; f < 10; f++) vk.Submit(new CommandList("golden idle frame", 1 << 8, log));
            check.Expect(vk.Staging.Bytes == left, $"L2: ten frames after the load the staging held is {vk.Staging.Bytes / MiB}MiB, not the {left / MiB}MiB the load left");
            for (int f = 0; f < 1000; f++) vk.Submit(new CommandList("golden idle frame", 1 << 8, log));
            vk.ReadBack(rb, got.AsSpan(0, 4));
            vk.Submit(new CommandList("golden idle frame", 1 << 8, log));
            check.Expect(vk.Staging.Bytes == (ulong)OneBlock, $"L2: a thousand idle frames on, the staging held is {vk.Staging.Bytes / MiB}MiB, not 64MiB");
        }
        finally
        {
            foreach (ImageId i in imgs) vk.Destroy(i);
            vk.Destroy(rb);
        }
    }

    /// <summary><paramref name="img"/> read back through <paramref name="rb"/> equals <paramref name="want"/> byte for byte.</summary>
    private static bool Arrives(VkBackend vk, ExtenderLog log, ImageId img, BufferId rb, byte[] want, byte[] got)
    {
        var cl = new CommandList("golden staging readback", 1 << 10, log);
        cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(0, 0, 0, 0), StagingSide, StagingSide, rb, 0));
        return vk.Submit(cl) && vk.ReadBack(rb, got) && got.AsSpan().SequenceEqual(want);
    }
}

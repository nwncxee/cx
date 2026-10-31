using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The alias groups past the approved examples: two members used in one submit each read back their own colour, a
/// group's allocation follows its largest member, and every way a lane can hold two live members at once is refused.
/// Oracles: hand-written clear colours, the device's own size for a lone image, and the refusal count before a case.
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
        AliasTwoLive(vk, check, log);
        AliasStaleStorageRead(vk, check, log);
        AliasLiveThroughRehome(vk, check, log, settle);
        AliasFollowsLargest(vk, check, log, settle, 4, 64, dropLarger: false);
        AliasFollowsLargest(vk, check, log, settle, 4, 64, dropLarger: true);
        vk.Destroy(settle);
    }

    /// <summary>Clear <paramref name="img"/> to <paramref name="c"/> in a pass of its own, into <paramref name="cl"/>.</summary>
    private static void ClearInto(CommandList cl, ImageId img, Vector4 c)
    {
        ReadOnlySpan<ColorAttachment> at = [new ColorAttachment(img, 0, 0, LoadOp.Clear, StoreOp.Store, c)];
        cl.BeginRenderPass(new RenderPassDesc(at));
        cl.EndRenderPass();
    }

    /// <summary>Holds the ordering between two members inside one submit, at a size whose copy outlasts a clear, and
    /// that the ordered use of two members refuses nothing; turns red when the barrier a claim places stops waiting
    /// on the other member's work, or when one member's write and read is taken for two live members.</summary>
    private static void AliasOneSubmit(VkBackend vk, Checker check, ExtenderLog log)
    {
        const int Side = 4096, Bytes = Side * Side * 4;
        ImageId first = Target(vk, Side, "golden alias one-submit first", "golden one"), second = Target(vk, Side, "golden alias one-submit second", "golden one");
        BufferId ra = vk.CreateBuffer(new BufferDesc(Bytes, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden alias one-submit read a"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(Bytes, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden alias one-submit read b"));
        try
        {
            check.Expect(!first.IsNull && !second.IsNull && !ra.IsNull && !rb.IsNull, "a one-submit alias target was refused (logged above)");
            if (first.IsNull || second.IsNull || ra.IsNull || rb.IsNull) return;
            vk.EndAliasFrame();
            int faults = vk.Faults;
            var cl = new CommandList("golden alias one submit", 1 << 10, log);
            ClearInto(cl, first, new Vector4(1, 0, 0, 1));
            cl.CopyImageToBuffer(new CopyToBufferCmd(first, new ImageRegion(0, 0, 0, 0), Side, Side, ra, 0));
            ClearInto(cl, second, new Vector4(0, 0, 1, 1));
            cl.CopyImageToBuffer(new CopyToBufferCmd(second, new ImageRegion(0, 0, 0, 0), Side, Side, rb, 0));
            var got = new byte[Bytes];
            check.Expect(vk.Submit(cl), "the one-submit alias list was refused (logged above)");
            check.Expect(vk.ReadBack(ra, got) && Solid(got, 255, 0, 0, 255), "inside one submit the first member does not read back its red");
            check.Expect(vk.ReadBack(rb, got) && Solid(got, 0, 0, 255, 255), "inside one submit the second member does not read back its blue");
            check.Expect(vk.Faults == faults, $"two members written and read in turn logged {vk.Faults - faults} refusals, not none");
        }
        finally
        {
            vk.Destroy(first); vk.Destroy(second); vk.Destroy(ra); vk.Destroy(rb);
        }
    }

    /// <summary>Holds that a member written while another member's write is unread is refused; turns red when a lane
    /// discards one member's bytes for another's without saying so.</summary>
    private static void AliasTwoLive(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId a = Target(vk, 4, "golden two live a", "golden two live"), b = Target(vk, 4, "golden two live b", "golden two live");
        BufferId rb = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden two live back"));
        try
        {
            check.Expect(!a.IsNull && !b.IsNull && !rb.IsNull, "a two-live-members target was refused (logged above)");
            if (a.IsNull || b.IsNull || rb.IsNull) return;
            vk.EndAliasFrame();
            int faults = vk.Faults;
            var cl = new CommandList("golden two live", 1 << 10, log);
            ClearInto(cl, a, new Vector4(1, 0, 0, 1));
            ClearInto(cl, b, new Vector4(0, 0, 1, 1));
            check.Expect(vk.Submit(cl), "the two-live-members list was refused (logged above)");
            check.Expect(vk.Faults == faults + 1,
                         $"a member written over an unread write logged {vk.Faults - faults} refusals, not 1");
            faults = vk.Faults;
            var back = new CommandList("golden two live read", 1 << 10, log);
            back.CopyImageToBuffer(new CopyToBufferCmd(a, new ImageRegion(0, 0, 0, 0), 4, 4, rb, 0));
            check.Expect(vk.Submit(back), "the read of the discarded member was refused (logged above)");
            check.Expect(vk.Faults == faults + 1,
                         $"reading the member whose bytes went logged {vk.Faults - faults} refusals, not 1");
        }
        finally { vk.Destroy(a); vk.Destroy(b); vk.Destroy(rb); }
    }

    /// <summary>Holds that a STORAGE read of a member another member took the memory from is refused; turns red when
    /// GENERAL is read as a write and the read starts from undefined memory with nothing said.</summary>
    private static void AliasStaleStorageRead(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId a = Target(vk, 4, "golden stale read a", "golden stale read", ImageUse.Storage);
        ImageId b = Target(vk, 4, "golden stale read b", "golden stale read", ImageUse.Storage);
        BufferId rb = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden stale read back"));
        try
        {
            check.Expect(!a.IsNull && !b.IsNull && !rb.IsNull, "a stale-read target was refused (logged above)");
            if (a.IsNull || b.IsNull || rb.IsNull) return;
            vk.EndAliasFrame();
            int faults = vk.Faults;
            var cl = new CommandList("golden stale read", 1 << 10, log);
            ClearInto(cl, a, new Vector4(1, 0, 0, 1));
            cl.CopyImageToBuffer(new CopyToBufferCmd(a, new ImageRegion(0, 0, 0, 0), 4, 4, rb, 0));
            ClearInto(cl, b, new Vector4(0, 0, 1, 1));
            check.Expect(vk.Submit(cl), "the stale-read list was refused (logged above)");
            check.Expect(vk.Faults == faults, $"writing one member, reading it and writing the other logged {vk.Faults - faults} refusals, not none");
            faults = vk.Faults;
            var read = new CommandList("golden stale storage read", 1 << 10, log);
            ReadOnlySpan<ImageId> one = [a];
            read.ReadImages(one, Access.StorageRead);
            check.Expect(vk.Submit(read), "the stale storage read was refused (logged above)");
            check.Expect(vk.Faults == faults + 1,
                         $"a storage read of the member whose memory went logged {vk.Faults - faults} refusals, not 1");
        }
        finally { vk.Destroy(a); vk.Destroy(b); vk.Destroy(rb); }
    }

    /// <summary>Holds that a group's memory moving under a member written this frame is refused, and so is the read
    /// that wanted those bytes; turns red when a mid-frame grow takes a live member's contents silently.</summary>
    private static void AliasLiveThroughRehome(VkBackend vk, Checker check, ExtenderLog log, BufferId settle)
    {
        ImageId a = Target(vk, 4, "golden rehome live small", "golden rehome live");
        BufferId rb = vk.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden rehome live back"));
        ImageId big = default;
        try
        {
            check.Expect(!a.IsNull && !rb.IsNull, "a rehome-under-a-live-member target was refused (logged above)");
            if (a.IsNull || rb.IsNull) return;
            vk.EndAliasFrame();
            var cl = new CommandList("golden rehome live", 1 << 10, log);
            ClearInto(cl, a, new Vector4(1, 0, 0, 1));
            check.Expect(vk.Submit(cl), "the rehome-under-a-live-member clear was refused (logged above)");
            int faults = vk.Faults;
            big = Target(vk, 64, "golden rehome live large", "golden rehome live");
            check.Expect(!big.IsNull, "the member that grows the group was refused (logged above)");
            check.Expect(vk.Faults == faults + 1,
                         $"the group's memory moved under a written member with {vk.Faults - faults} refusals, not 1");
            faults = vk.Faults;
            var after = new CommandList("golden rehome live read", 1 << 10, log);
            after.CopyImageToBuffer(new CopyToBufferCmd(a, new ImageRegion(0, 0, 0, 0), 4, 4, rb, 0));
            check.Expect(vk.Submit(after), "the read after the move was refused (logged above)");
            check.Expect(vk.Faults == faults + 1,
                         $"the read after the move logged {vk.Faults - faults} refusals, not 1");
        }
        finally
        {
            vk.Destroy(a); vk.Destroy(big); vk.Destroy(rb);
            vk.EndAliasFrame();
            Settle(vk, log, settle);
        }
    }

    /// <summary>Every RGBA8 texel of <paramref name="t"/> is the one colour given.</summary>
    internal static bool Solid(byte[] t, byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i < t.Length; i += 4)
            if (t[i] != r || t[i + 1] != g || t[i + 2] != b || t[i + 3] != a) return false;
        return true;
    }

    /// <summary>Holds a group's allocation at the size of its largest live member: it grows to a member made after a
    /// smaller one, and gives the memory back when the larger one goes. Oracle: the bytes the device charges for a
    /// lone image of the size the group should end at.</summary>
    private static void AliasFollowsLargest(VkBackend vk, Checker check, ExtenderLog log, BufferId settle,
                                            int small, int large, bool dropLarger)
    {
        int want = dropLarger ? small : large;
        Settle(vk, log, settle);
        (long Count, long Bytes) before = vk.Allocations;
        ImageId lone = Target(vk, want, "golden largest lone", null);
        long alone = vk.Allocations.Bytes - before.Bytes;
        vk.Destroy(lone);
        Settle(vk, log, settle);
        before = vk.Allocations;
        ImageId a = Target(vk, small, "golden largest small", "golden largest"),
                b = Target(vk, large, "golden largest large", "golden largest");
        if (dropLarger) vk.Destroy(b);
        vk.EndAliasFrame();
        Settle(vk, log, settle);
        long held = vk.Allocations.Bytes - before.Bytes;
        check.Expect(!a.IsNull && !b.IsNull, "a target of the largest-member group was refused (logged above)");
        check.Expect(held == alone, $"a group of a {small}x{small} and a {large}x{large}"
                                  + $"{(dropLarger ? " whose larger went" : "")} holds {held} bytes, not the {alone} a lone {want}x{want} takes");
        vk.Destroy(a);
        if (!dropLarger) vk.Destroy(b);
        vk.EndAliasFrame();
        Settle(vk, log, settle);
    }
}

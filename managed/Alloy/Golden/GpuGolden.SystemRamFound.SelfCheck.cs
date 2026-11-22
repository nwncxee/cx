using System.Collections.Generic;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Found tests on the system-RAM warning, on a device of their own capped as the approved examples' is: the
/// path the mutation pass showed no example reached. Oracle: the warning lines a tapped log hears.</summary>
internal static partial class GpuGolden
{
    /// <summary>Side of the move-back test's one alias member: 2048 x 2048 RGBA8, 16MiB, which fits the 64MiB cap alone.</summary>
    private const int BackSide = 2048;
    /// <summary>MiB the filler leaves the capped heap, under the member's 16MiB, so the member lands in system RAM.</summary>
    private const long BackLeaveMiB = 8;

    /// <summary>The found system-RAM goldens, judged by the suite's <paramref name="check"/>.</summary>
    private static void SystemRamFound(Checker check, ExtenderLog log)
    {
        var heard = new List<string>();
        var ear = new ExtenderLog { Tap = heard.Add };
        VkBackend? capped = VkBackend.TryCreate(ear, capMib: SysRamCapMib);
        check.Expect(capped != null, $"system RAM found: a device capped at {SysRamCapMib}MiB was refused: {string.Join(" | ", heard)}");
        if (capped == null) return;
        BufferId rb = capped.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden sysram found read"));
        try
        {
            if (rb.IsNull) { check.Expect(false, "system RAM found: the capped device's readback was refused"); return; }
            Settle(capped, log, rb);
            SpilledAgainWarned(capped, check, ear, heard, rb);
            GroupMovesBack(capped, check, ear, rb);
        }
        finally
        {
            if (!rb.IsNull) capped.Destroy(rb);
            capped.Dispose();
        }
    }

    // Given an alias group warned about in system RAM, and back in VRAM since
    // When it lands in system RAM again and the next frame line is written
    // Then cx.log warns about it again, once
    private static void SpilledAgainWarned(VkBackend vk, Checker check, ExtenderLog ear, List<string> heard, BufferId rb)
    {
        const string Group = "golden sysram again";
        ImageId small = Target(vk, 1024, "golden sysram again small", Group);
        ImageId big = HalfTarget(vk, "golden sysram again big", Group);
        try
        {
            check.Expect(!small.IsNull && !big.IsNull, "system RAM again: a member of the 128MiB group was refused");
            if (small.IsNull || big.IsNull) return;
            heard.Clear();
            AlloyRenderer.FrameVram(vk, ear);
            check.Expect(Count(heard, Group) == 1, $"system RAM again: the group past the cap was not warned about once: {string.Join(" | ", heard)}");
            vk.Destroy(big);
            big = default;
            vk.EndAliasFrame();
            Settle(vk, ear, rb);
            string back = AlloyRenderer.FrameVram(vk, ear);
            check.Expect(AlloyRenderer.ClauseMiB(back, "in system RAM") == 0, $"system RAM again: the shrunk group is not back in VRAM: \"{back}\"");
            big = HalfTarget(vk, "golden sysram again big", Group);
            check.Expect(!big.IsNull, "system RAM again: the regrown member was refused");
            if (big.IsNull) return;
            heard.Clear();
            string line = AlloyRenderer.FrameVram(vk, ear);
            AlloyRenderer.FrameVram(vk, ear);
            check.Expect(AlloyRenderer.ClauseMiB(line, "in system RAM") >= 128 && Count(heard, Group) == 1,
                         $"system RAM again: the regrown group in system RAM was not warned about once more: \"{line}\" | {string.Join(" | ", heard)}");
        }
        finally
        {
            if (!small.IsNull) vk.Destroy(small);
            if (!big.IsNull) vk.Destroy(big);
            Settle(vk, ear, rb);
        }
    }

    // Given an alias group that landed in system RAM while the capped heap was full, and nothing else there
    // When the heap is emptied, a move-back check runs and the next frame is submitted
    // Then the group is back in device memory
    private static void GroupMovesBack(VkBackend vk, Checker check, ExtenderLog ear, BufferId rb)
    {
        BufferId fill = Filler(vk, BackLeaveMiB * MiB, "golden sysram back filler");
        ImageId member = Target(vk, BackSide, "golden sysram back member", "golden sysram back");
        try
        {
            check.Expect(!fill.IsNull && !member.IsNull && vk.InSystemRam(member),
                         $"system RAM back: precondition: filler {!fill.IsNull}, member {!member.IsNull}, in system RAM {!member.IsNull && vk.InSystemRam(member)}");
            if (fill.IsNull || member.IsNull) return;
            vk.Destroy(fill);
            fill = default;
            Settle(vk, ear, rb);
            vk.MoveBackCheck();
            Settle(vk, ear, rb);
            check.Expect(!vk.InSystemRam(member), "system RAM back: the alias group is still in system RAM after room returned and a move-back check ran");
        }
        finally
        {
            if (!fill.IsNull) vk.Destroy(fill);
            if (!member.IsNull) vk.Destroy(member);
            Settle(vk, ear, rb);
        }
    }
}

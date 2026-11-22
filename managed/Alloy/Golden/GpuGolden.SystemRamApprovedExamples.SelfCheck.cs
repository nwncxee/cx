using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Approved-spec: the frame line's "in system RAM" clause and its warning, on a second device whose
/// device-local heaps are capped at <see cref="SysRamCapMib"/>. Oracles: hand-written allocation sizes past and
/// within the cap, the labels given here, and the warning lines a tapped log hears.</summary>
internal static partial class GpuGolden
{
    /// <summary>The cap on the second device's device-local heaps: an allocation past it has only system RAM left.</summary>
    private const long SysRamCapMib = 64;

    /// <summary>Every system-RAM golden on one capped device, the staging one first while nothing has spilled.</summary>
    private static void SystemRamSelfCheck(Checker check, ExtenderLog log)
    {
        var heard = new List<string>();
        var ear = new ExtenderLog { Tap = heard.Add };
        VkBackend? capped = VkBackend.TryCreate(ear, capMib: SysRamCapMib);
        check.Expect(capped != null, $"system RAM: a device capped at {SysRamCapMib}MiB was refused: {string.Join(" | ", heard)}");
        if (capped == null) return;
        BufferId rb = capped.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden sysram read"));
        try
        {
            if (rb.IsNull) { check.Expect(false, "system RAM: the capped device's readback was refused"); return; }
            Settle(capped, log, rb);
            StagingNotWarned(capped, check, ear, heard, rb);
            SpillWarned(capped, check, ear, heard, rb);
            SecondWarnedOnly(capped, check, ear, heard, rb);
            GroupWarned(capped, check, ear, heard, rb);
            BackInVram(capped, check, ear, heard, rb);
        }
        finally
        {
            if (!rb.IsNull) capped.Destroy(rb);
            capped.Dispose();
        }
    }

    /// <summary>Tapped lines naming <paramref name="label"/>.</summary>
    private static int Naming(List<string> heard, string label)
    {
        int n = 0;
        foreach (string s in heard) if (s.Contains(label, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The one tapped line naming <paramref name="label"/> also gives <paramref name="mib"/> as MiB.</summary>
    private static bool NamesSize(List<string> heard, string label, long mib)
    {
        foreach (string s in heard)
            if (s.Contains(label, StringComparison.Ordinal)
                && (s.Contains($"{mib}MiB", StringComparison.Ordinal) || s.Contains($"{mib} MiB", StringComparison.Ordinal))) return true;
        return false;
    }

    // Given only staging and readback buffers are in system RAM
    // When the next frame line is written
    // Then it shows in system RAM 0 MiB, and nothing is warned
    private static void StagingNotWarned(VkBackend vk, Checker check, ExtenderLog ear, List<string> heard, BufferId rb)
    {
        BufferId up = vk.CreateBuffer(new BufferDesc(96 * MiB, BufferUse.CopySrc, MemoryKind.Upload, "golden sysram upload"));
        BufferId down = vk.CreateBuffer(new BufferDesc(96 * MiB, BufferUse.CopyDst, MemoryKind.Readback, "golden sysram readback"));
        try
        {
            check.Expect(!up.IsNull && !down.IsNull, "system RAM staging: a 96MiB upload or readback buffer was refused");
            heard.Clear();
            string line = AlloyRenderer.FrameVram(vk, ear);
            check.Expect(AlloyRenderer.ClauseMiB(line, "in system RAM") == 0,
                         $"system RAM staging: with only 96MiB of upload and 96MiB of readback in system RAM the line does not show in system RAM 0 MiB: \"{line}\"");
            check.Expect(Naming(heard, "golden sysram upload") == 0 && Naming(heard, "golden sysram readback") == 0,
                         $"system RAM staging: a staging or readback buffer was warned about: {string.Join(" | ", heard)}");
        }
        finally
        {
            if (!up.IsNull) vk.Destroy(up);
            if (!down.IsNull) vk.Destroy(down);
            Settle(vk, ear, rb);
        }
    }

    // Given an allocation other than staging or readback lands in system RAM
    // When the next frame line is written
    // Then it shows in system RAM and how many MiB, and cx.log warns once, naming the allocation's label and size
    private static void SpillWarned(VkBackend vk, Checker check, ExtenderLog ear, List<string> heard, BufferId rb)
    {
        BufferId big = vk.CreateBuffer(new BufferDesc(96 * MiB, BufferUse.Storage, MemoryKind.Device, "golden sysram spill"));
        try
        {
            check.Expect(!big.IsNull, $"system RAM spill: a 96MiB buffer past the {SysRamCapMib}MiB cap was refused rather than placed in system RAM");
            if (big.IsNull) return;
            heard.Clear();
            string line = AlloyRenderer.FrameVram(vk, ear);
            string again = AlloyRenderer.FrameVram(vk, ear);
            check.Expect(AlloyRenderer.ClauseMiB(line, "in system RAM") >= 96 && AlloyRenderer.ClauseMiB(again, "in system RAM") >= 96,
                         $"system RAM spill: a 96MiB buffer past the cap, and the line does not show at least 96 MiB in system RAM: \"{line}\"");
            check.Expect(Naming(heard, "golden sysram spill") == 1 && NamesSize(heard, "golden sysram spill", 96),
                         $"system RAM spill: over two frame lines cx.log did not warn exactly once naming golden sysram spill and 96MiB: {string.Join(" | ", heard)}");
        }
        finally
        {
            if (!big.IsNull) vk.Destroy(big);
            Settle(vk, ear, rb);
        }
    }

    // Given one allocation already warned, and a second lands in system RAM
    // When the next frame line is written
    // Then cx.log warns about the second only
    private static void SecondWarnedOnly(VkBackend vk, Checker check, ExtenderLog ear, List<string> heard, BufferId rb)
    {
        BufferId first = vk.CreateBuffer(new BufferDesc(96 * MiB, BufferUse.Storage, MemoryKind.Device, "golden sysram first"));
        BufferId second = default;
        try
        {
            check.Expect(!first.IsNull, "system RAM second: the first 96MiB buffer was refused");
            if (first.IsNull) return;
            heard.Clear();
            AlloyRenderer.FrameVram(vk, ear);
            check.Expect(Naming(heard, "golden sysram first") == 1, $"system RAM second: the first allocation in system RAM was not warned about once: {string.Join(" | ", heard)}");
            second = vk.CreateBuffer(new BufferDesc(96 * MiB, BufferUse.Storage, MemoryKind.Device, "golden sysram second"));
            check.Expect(!second.IsNull, "system RAM second: the second 96MiB buffer was refused");
            if (second.IsNull) return;
            heard.Clear();
            AlloyRenderer.FrameVram(vk, ear);
            check.Expect(Naming(heard, "golden sysram second") == 1 && Naming(heard, "golden sysram first") == 0,
                         $"system RAM second: the next frame line's warnings are not about the second allocation only: {string.Join(" | ", heard)}");
        }
        finally
        {
            if (!first.IsNull) vk.Destroy(first);
            if (!second.IsNull) vk.Destroy(second);
            Settle(vk, ear, rb);
        }
    }

    /// <summary>A 4096x4096 half-float target, 128MiB, in alias group <paramref name="alias"/>.</summary>
    private static ImageId HalfTarget(VkBackend vk, string label, string alias)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, 4096, 4096, 1, 1, 1, 1,
                                        ImageUse.ColorTarget | ImageUse.Sampled | ImageUse.CopySrc, label, alias));

    // Given an alias group lands in system RAM
    // When the next frame line is written
    // Then it shows its MiB in system RAM, and cx.log warns once, naming the group and its size
    private static void GroupWarned(VkBackend vk, Checker check, ExtenderLog ear, List<string> heard, BufferId rb)
    {
        ImageId a = HalfTarget(vk, "golden sysram member a", "golden sysram group");
        ImageId b = HalfTarget(vk, "golden sysram member b", "golden sysram group");
        try
        {
            check.Expect(!a.IsNull && !b.IsNull, "system RAM group: a 128MiB member of the group past the cap was refused");
            if (a.IsNull || b.IsNull) return;
            heard.Clear();
            string line = AlloyRenderer.FrameVram(vk, ear);
            AlloyRenderer.FrameVram(vk, ear);
            check.Expect(AlloyRenderer.ClauseMiB(line, "in system RAM") >= 128,
                         $"system RAM group: a 128MiB alias group past the cap, and the line does not show at least 128 MiB in system RAM: \"{line}\"");
            check.Expect(Naming(heard, "golden sysram group") == 1 && NamesSize(heard, "golden sysram group", 128),
                         $"system RAM group: over two frame lines cx.log did not warn exactly once naming golden sysram group and 128MiB: {string.Join(" | ", heard)}");
        }
        finally
        {
            if (!a.IsNull) vk.Destroy(a);
            if (!b.IsNull) vk.Destroy(b);
            Settle(vk, ear, rb);
        }
    }

    // Given an allocation that was in system RAM is back in VRAM
    // When the next frame line is written
    // Then it shows in system RAM 0 MiB
    private static void BackInVram(VkBackend vk, Checker check, ExtenderLog ear, List<string> heard, BufferId rb)
    {
        ImageId small = Target(vk, 1024, "golden sysram back small", "golden sysram back");
        ImageId big = HalfTarget(vk, "golden sysram back big", "golden sysram back");
        try
        {
            check.Expect(!small.IsNull && !big.IsNull, "system RAM back: a member of the 128MiB group was refused");
            if (small.IsNull || big.IsNull) return;
            string spilled = AlloyRenderer.FrameVram(vk, ear);
            check.Expect(AlloyRenderer.ClauseMiB(spilled, "in system RAM") >= 128,
                         $"system RAM back: the 128MiB group past the cap does not first show in system RAM: \"{spilled}\"");
            vk.Destroy(big);
            big = default;
            vk.EndAliasFrame();
            Settle(vk, ear, rb);
            string line = AlloyRenderer.FrameVram(vk, ear);
            check.Expect(AlloyRenderer.ClauseMiB(line, "in system RAM") == 0,
                         $"system RAM back: the group moved onto the 4MiB its last member needs, within the cap, and the line does not show in system RAM 0 MiB: \"{line}\"");
        }
        finally
        {
            if (!small.IsNull) vk.Destroy(small);
            if (!big.IsNull) vk.Destroy(big);
            Settle(vk, ear, rb);
        }
    }
}

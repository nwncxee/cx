using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>The cap's own figure, MiB, the approved examples name.</summary>
    private const long CapMib = 3072;

    /// <summary>The VRAM cap's approved examples, each on a device of its own made with <c>alloy.vramcap</c>'s value.
    /// Oracles: the uncapped golden device's own heap size and driver budget (a second device on the same card), and
    /// the hand-written figures a 3072 MiB card reads as: a 768 MiB texture budget and at most an 8-page lightmap pool.</summary>
    private static void VramCapApprovedSelfCheck(VkBackend vk, ExtenderLog log)
    {
        var check = new Checker(log, "GpuGolden.VramCap.SelfCheck");
        vk.DeviceLocalBytes(out _, out long cardBudget, out long cardSize, out _);
        check.Expect(Renderer.VramCap.TryParse("3072", out int cap) && cap == CapMib, "vramcap: cx.cfg's alloy.vramcap 3072 does not read as 3072");
        if (cardSize >> 20 <= CapMib || cardBudget >> 20 <= CapMib || !vk.MemoryBudget)
        {
            check.Skip($"vramcap: the examples need a card past {CapMib}MiB whose driver reports its budget; this one has {cardSize >> 20}MiB, "
                       + $"a budget of {cardBudget >> 20}MiB, VK_EXT_memory_budget {vk.MemoryBudget}");
            return;
        }
        VramCapBoots(check, log, cap);
        VramCapDefault(check, log, cardSize, cardBudget);
        VramCapAbove(check, log, cardSize);
        if (vk.DeviceLocalHeaps >= 2) VramCapTwoHeaps(check, log, cap);
        else check.Skip($"vramcap: the two-heap example needs a card with two device-local heaps; this one has {vk.DeviceLocalHeaps}");
        log.Info(check.Ok ? "GpuGolden.VramCap.SelfCheck: passed" : "GpuGolden.VramCap.SelfCheck: FAILED");
    }

    /// <summary>A device made with <paramref name="capMib"/>, with the lines its creation wrote; the lines are logged
    /// again once the tap is off, so cx.log still holds them.</summary>
    private static VkBackend? CappedDevice(ExtenderLog log, long capMib, List<string> heard)
    {
        Action<string>? tap = log.Tap;
        log.Tap = heard.Add;
        VkBackend? dev;
        try { dev = VkBackend.TryCreate(log, capMib: capMib); }
        finally { log.Tap = tap; }
        foreach (string line in heard) log.Info(line);
        return dev;
    }

    /// <summary>Lines in <paramref name="heard"/> that name <c>alloy.vramcap</c> and hold every one of <paramref name="words"/>.</summary>
    private static int CapLines(List<string> heard, params string[] words)
    {
        int n = 0;
        foreach (string line in heard)
            if (line.Contains("alloy.vramcap", StringComparison.Ordinal) && Array.TrueForAll(words, w => line.Contains(w, StringComparison.Ordinal))) n++;
        return n;
    }

    /// <summary>The 3072 MiB device: the frame line's budget, the log line, the texture budget, the lightmap pool.</summary>
    private static void VramCapBoots(Checker check, ExtenderLog log, int cap)
    {
        var heard = new List<string>();
        VkBackend? capped = CappedDevice(log, cap, heard);
        check.Expect(capped != null, "vramcap: the device made with alloy.vramcap 3072 was refused (logged above)");
        if (capped == null) return;
        try
        {
            capped.DeviceLocalBytes(out long usage, out long budget, out long size, out long held);
            long sizeMib = size >> 20, freeMib = Math.Max(budget - usage, 0) >> 20;

            // Given cx.cfg sets alloy.vramcap 3072 on an 8 GB card
            // When the client boots
            // Then the frame line's budget reads 3072 MiB, and cx.log says once that the cap is in force
            string line = AlloyRenderer.VramLine(held, usage, budget, capped.Largest(3));
            check.Expect(line.Contains("/3072MiB of the budget used", StringComparison.Ordinal),
                         $"vramcap E1: with alloy.vramcap 3072 the frame line's budget does not read 3072MiB: \"{line}\"");
            int said = CapLines(heard, "3072", "in force");
            check.Expect(said == 1 && CapLines(heard, "no cap") == 0, $"vramcap E1: cx.log says {said} times, not once, that alloy.vramcap 3072 is in force");

            // Given alloy.vramcap 3072 on an 8 GB card
            // When the texture budgets are worked out
            // Then they read 3072 MiB as the card's size
            long texBudget = TexRegistry.BudgetFor(Renderer.TexCache.Default, sizeMib, freeMib) >> 20;
            check.Expect(sizeMib == 3072 && texBudget == 768,
                         $"vramcap E5: the texture budget reads a {sizeMib}MiB card and comes to {texBudget}MiB, not a 3072MiB card's 768MiB");

            // Given alloy.vramcap 3072
            // When the lightmap pool is sized
            // Then it is sized from 3072 MiB
            int pages = LightmapBake.PoolPages(Lightmap.Vram.Default, sizeMib, freeMib);
            check.Expect(sizeMib == 3072 && pages <= 8,
                         $"vramcap E6: the lightmap pool is {pages} pages from a {sizeMib}MiB card, not at most the 8 (768MiB, a quarter) a 3072MiB card gives");
        }
        finally { capped.Dispose(); }
    }

    /// <summary>The default: no cap, the driver's own budget and the card's own size, and no word of a cap.</summary>
    private static void VramCapDefault(Checker check, ExtenderLog log, long cardSize, long cardBudget)
    {
        // Given alloy.vramcap is 0, the default
        // When the client boots
        // Then the budget is the driver's own figure, as today
        var heard = new List<string>();
        check.Expect(Renderer.VramCap.Default == 0, $"vramcap E2: alloy.vramcap defaults to {Renderer.VramCap.Default}, not 0");
        VkBackend? plain = CappedDevice(log, Renderer.VramCap.Default, heard);
        check.Expect(plain != null, "vramcap E2: the device made with alloy.vramcap 0 was refused (logged above)");
        if (plain == null) return;
        try
        {
            plain.DeviceLocalBytes(out _, out long budget, out long size, out _);
            long drift = Math.Abs(budget - cardBudget) >> 20;
            check.Expect(size == cardSize && drift <= 256,
                         $"vramcap E2: uncapped, the device reads {size >> 20}MiB with a {budget >> 20}MiB budget, not the card's {cardSize >> 20}MiB and the driver's {cardBudget >> 20}MiB");
            check.Expect(CapLines(heard) == 0, "vramcap E2: with alloy.vramcap 0, cx.log speaks of a cap");
        }
        finally { plain.Dispose(); }
    }

    /// <summary>A cap past the card's size caps nothing, and says so once.</summary>
    private static void VramCapAbove(Checker check, ExtenderLog log, long cardSize)
    {
        // Given alloy.vramcap above the card's size
        // When the device is created
        // Then no cap is in force and cx.log says so
        var heard = new List<string>();
        long above = (cardSize >> 20) + 1024;
        VkBackend? dev = CappedDevice(log, above, heard);
        check.Expect(dev != null, $"vramcap E8: the device made with alloy.vramcap {above} was refused (logged above)");
        if (dev == null) return;
        try
        {
            dev.DeviceLocalBytes(out _, out _, out long size, out _);
            check.Expect(size == cardSize, $"vramcap E8: alloy.vramcap {above} on a {cardSize >> 20}MiB card reads {size >> 20}MiB, not the card's own");
            int said = CapLines(heard, "no cap");
            check.Expect(said == 1, $"vramcap E8: cx.log says {said} times, not once, that alloy.vramcap {above} puts no cap in force");
        }
        finally { dev.Dispose(); }
    }

    /// <summary>Two device-local heaps share the one cap.</summary>
    private static void VramCapTwoHeaps(Checker check, ExtenderLog log, int cap)
    {
        // Given a card with two device-local heaps and alloy.vramcap 3072
        // When the device is created
        // Then the two heaps together hold 3072 MiB
        var heard = new List<string>();
        VkBackend? dev = CappedDevice(log, cap, heard);
        check.Expect(dev != null, "vramcap E7: the device made with alloy.vramcap 3072 was refused (logged above)");
        if (dev == null) return;
        try
        {
            dev.DeviceLocalBytes(out _, out _, out long size, out _);
            check.Expect(size >> 20 == 3072 && (size & ((1 << 20) - 1)) == 0,
                         $"vramcap E7: the {dev.DeviceLocalHeaps} device-local heaps together hold {size >> 20}MiB under alloy.vramcap 3072, not 3072MiB");
        }
        finally { dev.Dispose(); }
    }
}

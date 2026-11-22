using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>The VRAM cap's approved examples, each on a device of its own made with <c>alloy.vramcap</c>'s value.
    /// Oracles: the uncapped golden device's own heap size (a second device on the same card), the hand-written
    /// 3072 MiB, and the budgets the pure sizing functions give for a card of exactly that size.</summary>
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
        VramCapDefault(check, log, cardSize);
        VramCapAbove(check, log, cardSize, (cardSize >> 20) + 1024);
        if ((cardSize & ((1 << 20) - 1)) == 0) VramCapAbove(check, log, cardSize, cardSize >> 20);
        else check.Skip($"vramcap: the at-the-card example needs a card of whole MiB; this one has {cardSize} bytes");
        long[] heaps = vk.DeviceLocalHeapSizes();
        if (heaps.Length >= 2) VramCapTwoHeaps(check, log, cap, heaps);
        else check.Skip($"vramcap: the two-heap example needs a card with two device-local heaps; this one has {heaps.Length}");
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

    /// <summary>The 3072 MiB device: the frame line's budget, the log line, the texture budget, the lightmap pool.</summary>
    private static void VramCapBoots(Checker check, ExtenderLog log, int cap)
    {
        var heard = new List<string>();
        VkBackend? capped = CappedDevice(log, cap, heard);
        check.Expect(capped != null, "vramcap: the device made with alloy.vramcap 3072 was refused (logged above)");
        if (capped == null) return;
        try
        {
            // Given cx.cfg sets alloy.vramcap 3072 on an 8 GB card
            // When the client boots
            // Then the frame line's budget reads 3072 MiB, and cx.log says once that the cap is in force
            string line = AlloyRenderer.FrameVram(capped, log);
            check.Expect(line.Contains($"/{CapMib}MiB of the budget", StringComparison.Ordinal),
                         $"vramcap E1: with alloy.vramcap 3072 the frame line's budget does not read 3072MiB: \"{line}\"");
            int said = Count(heard, VkBackend.CapTag(CapMib));
            check.Expect(said == 1 && Count(heard, VkBackend.NoCapTag) == 0, $"vramcap E1: cx.log says {said} times, not once, that alloy.vramcap 3072 is in force");

            // Given alloy.vramcap 3072 on an 8 GB card
            // When the texture budgets are worked out
            // Then they read 3072 MiB as the card's size
            (long sizeMib, long freeMib, long texBudget, int pages) = AlloyRenderer.WithVramOf(capped,
                () => (AlloyRenderer.VramTotalMib, AlloyRenderer.VramFreeMib, TexRegistry.BudgetBytes() >> 20, LightmapBake.PoolPagesNow()));
            long texWant = TexRegistry.BudgetFor(Renderer.TexCache.Boot, CapMib, freeMib) >> 20;
            check.Expect(sizeMib == CapMib && texBudget == texWant,
                         $"vramcap E5: the texture budget reads a {sizeMib}MiB card and comes to {texBudget}MiB, not a 3072MiB card's {texWant}MiB");

            // Given alloy.vramcap 3072
            // When the lightmap pool is sized
            // Then it is sized from 3072 MiB
            int pagesWant = LightmapBake.PoolPages(Lightmap.Vram.V, CapMib, freeMib);
            check.Expect(sizeMib == CapMib && pages == pagesWant,
                         $"vramcap E6: the lightmap pool is {pages} pages from a {sizeMib}MiB card, not the {pagesWant} a 3072MiB card gives");
        }
        finally { capped.Dispose(); }
    }

    /// <summary>The default: no cap, the driver's own budget and the card's own size, and no word of a cap.</summary>
    private static void VramCapDefault(Checker check, ExtenderLog log, long cardSize)
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
            check.Expect(size == cardSize && budget >> 20 > CapMib,
                         $"vramcap E2: uncapped, the device reads {size >> 20}MiB with a {budget >> 20}MiB budget, not the card's {cardSize >> 20}MiB with a budget past {CapMib}MiB");
            check.Expect(Count(heard, VkBackend.CapPrefix) == 0, "vramcap E2: with alloy.vramcap 0, cx.log speaks of a cap");
        }
        finally { plain.Dispose(); }
    }

    /// <summary>A cap of <paramref name="above"/> MiB, at or past the card's size, caps nothing, and says so once.</summary>
    private static void VramCapAbove(Checker check, ExtenderLog log, long cardSize, long above)
    {
        // Given alloy.vramcap at or above the card's size
        // When the device is created
        // Then no cap is in force and cx.log says so
        var heard = new List<string>();
        VkBackend? dev = CappedDevice(log, above, heard);
        check.Expect(dev != null, $"vramcap E8: the device made with alloy.vramcap {above} was refused (logged above)");
        if (dev == null) return;
        try
        {
            dev.DeviceLocalBytes(out _, out _, out long size, out _);
            check.Expect(size == cardSize, $"vramcap E8: alloy.vramcap {above} on a {cardSize >> 20}MiB card reads {size >> 20}MiB, not the card's own");
            int said = Count(heard, VkBackend.NoCapTag);
            check.Expect(said == 1, $"vramcap E8: cx.log says {said} times, not once, that alloy.vramcap {above} puts no cap in force");
        }
        finally { dev.Dispose(); }
    }

    /// <summary>Two device-local heaps share the one cap, each by its size on the uncapped card,
    /// <paramref name="card"/>.</summary>
    private static void VramCapTwoHeaps(Checker check, ExtenderLog log, int cap, long[] card)
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
            long[] shares = dev.DeviceLocalHeapSizes();
            check.Expect(size >> 20 == CapMib && (size & ((1 << 20) - 1)) == 0,
                         $"vramcap E7: the {shares.Length} device-local heaps together hold {size >> 20}MiB under alloy.vramcap 3072, not 3072MiB");

            // Found, not approved: no heap is starved for another's sake; each holds its share of the cap by its size
            long whole = 0;
            foreach (long h in card) whole += h;
            for (int i = 0; i < card.Length && i < shares.Length; i++)
            {
                long want = (long)((Int128)CapMib * card[i] / whole);
                check.Expect(Math.Abs((shares[i] >> 20) - want) <= 1,
                             $"vramcap E7: device-local heap {i}, {card[i] >> 20}MiB of the card's {whole >> 20}MiB, holds {shares[i] >> 20}MiB of the cap, not its {want}MiB share");
            }
        }
        finally { dev.Dispose(); }
    }
}

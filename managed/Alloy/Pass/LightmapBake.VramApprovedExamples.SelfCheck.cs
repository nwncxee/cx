using System;
using System.IO;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Approved examples of the VRAM ladder on the bake's own Vulkan path: the scratch it takes, frees and waits
/// for, and pages and cache keys that do not move with it. Oracles: the scratch's MiB worked by hand from its images,
/// a hand-written cap and ballast, and a second bake of the same fixture.</summary>
internal static unsafe partial class LightmapBake
{
    private const long GoldMiB = 1 << 20;
    /// <summary>The whole scratch by hand: three RGBA16F planes and a D16 depth at 4096², two RGBA32UI block images at 1024².</summary>
    private const long FullScratchMiB = 448;
    /// <summary>Half of <see cref="FullScratchMiB"/>, with room for the device's alignment.</summary>
    private const long HalfScratchMiB = 232;
    private const long GoldBudget = 8000, GoldPast = 7360, GoldBand = 6800, GoldUnder = 5600;

    /// <summary>Every bake example on <paramref name="vk"/>, with the bake's own state put back after each.</summary>
    internal static void VramGoldens(VkBackend vk, Checker check, ExtenderLog log)
    {
        vk.DeviceLocalBytes(out long usage, out long budget, out _, out _);
        if (budget - usage < 1024 * GoldMiB) { check.Skip($"the bake's VRAM examples need 1024 MiB free; the device has {(budget - usage) / GoldMiB} MiB"); return; }
        int cube0 = CasterPass.CubeFaceSize, dyn0 = CasterPass.CubeDynFaceSize;
        Guarded(vk, log, cube0, dyn0, () => HalfWhenPast(vk, check));
        Guarded(vk, log, cube0, dyn0, () => FreedIdle(vk, check));
        Guarded(vk, log, cube0, dyn0, () => FullUnder(vk, check));
        Guarded(vk, log, cube0, dyn0, () => WaitsThenGoesOn(check, log));
        Guarded(vk, log, cube0, dyn0, () => LoweredKeysHit(vk, check, log, cube0));
        Guarded(vk, log, cube0, dyn0, () => HalfMatchesFull(vk, check, log));
    }

    /// <summary>Run one example on a fresh bake state on <paramref name="vk"/> and a freshly armed ladder; put back
    /// the bake's state, its stand-down and the ladder after.</summary>
    private static void Guarded(VkBackend vk, ExtenderLog log, int cube, int dyn, Action body)
    {
        BakeVkState saved = _vkb;
        int page = _bakePage;
        bool down = _down.Failed;
        ExtenderLog? oldLog = _log;
        _vkb = new BakeVkState { Dev = vk };
        _bakePage = -1;
        _log ??= log;
        VramLadder.Reset(null, cube, dyn);
        try { body(); }
        finally
        {
            _bakePage = -1;
            VkFreeScratch();
            _vkb = saved; _bakePage = page; _log = oldLog;
            if (!down) _down.Forget();
            VramLadder.AreaChanged(0, GoldBudget, 0);
            VramLadder.Reset(null, cube, dyn);
        }
    }

    /// <summary>The MiB <see cref="VkScratch"/> allocated on the bake's device as a page opens.</summary>
    private static long ScratchMib()
    {
        VkBackend vk = _vkb.Dev!;
        long before = vk.Allocations.Bytes;
        VkScratch();
        return (vk.Allocations.Bytes - before) / GoldMiB;
    }

    // Given use is still past 90% when an area's lightmap bake starts
    // When the bake allocates its scratch
    // Then it takes half the scratch and bakes in more steps, and if even that does not fit, the bake waits and the area draws with the lightmaps it has
    /// <summary>E2, half the scratch. Oracle: the scratch's MiB by hand, 448 whole and at most 232 halved.</summary>
    private static void HalfWhenPast(VkBackend vk, Checker check)
    {
        VramLadder.Period(GoldPast, GoldBudget);
        VramLadder.Period(GoldPast, GoldBudget);
        long mib = ScratchMib();
        check.Expect(VramLadder.HalfScratch && mib > 0 && mib <= HalfScratchMiB,
                     $"E2: past 90% the bake's scratch took {mib} MiB, not half of the {FullScratchMiB} MiB whole (at most {HalfScratchMiB})");
    }

    // Given use is still past 90% when an area's lightmap bake starts
    // When the bake allocates its scratch
    // Then it takes half the scratch and bakes in more steps, and if even that does not fit, the bake waits and the area draws with the lightmaps it has
    // Given a bake waiting for scratch
    // When use falls under 90% in the same area
    // Then the bake goes on with no area change
    /// <summary>E2's wait and E11, one run: a device capped at 512 MiB holding 400 MiB of ballast has no room for
    /// even the half scratch. Oracle: the hand-written cap and ballast, and the ballast's release.</summary>
    private static void WaitsThenGoesOn(Checker check, ExtenderLog log)
    {
        VkBackend? capped = VkBackend.TryCreate(log, capMib: 512);
        check.Expect(capped != null, "E2: the 512 MiB capped device was refused (logged above)");
        if (capped == null) return;
        BakeVkState mine = _vkb;
        _vkb = new BakeVkState { Dev = capped };
        BufferId ballast = capped.CreateBuffer(new BufferDesc(400 * GoldMiB, BufferUse.Storage, MemoryKind.Device, "golden vram ballast"));
        BufferId rb = capped.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden vram read"));
        Span<byte> four = stackalloc byte[4];
        try
        {
            check.Expect(!ballast.IsNull, "E2: the 400 MiB ballast did not fit under the 512 MiB cap");
            if (ballast.IsNull) return;
            VramLadder.Period(GoldPast, GoldBudget);
            VramLadder.Period(GoldPast, GoldBudget);
            VkScratch();
            check.Expect(ScratchWaits && !_vkb.Scratched && !_down.Failed,
                         $"E2: with no room for half the scratch the bake did not wait (waits {ScratchWaits}, scratch {_vkb.Scratched}, stood down {_down.Failed})");
            capped.Destroy(ballast); ballast = default;
            for (int i = 0; i < 6; i++) { capped.Submit(new CommandList("golden vram settle", 1 << 8, log)); capped.ReadBack(rb, four); }
            VramLadder.Period(GoldBand, GoldBudget);
            bool went = VkScratch();
            check.Expect(went && _vkb.Scratched && !ScratchWaits && !_down.Failed,
                         $"E11: under 90% in the same area the waiting bake did not go on (scratch {_vkb.Scratched}, waits {ScratchWaits})");
        }
        finally
        {
            if (!ballast.IsNull) capped.Destroy(ballast);
            capped.Destroy(rb);
            _bakePage = -1;
            VkFreeScratch();
            _vkb = mine;
            capped.Dispose();
        }
    }

    // Given use past 90% after textures, with the bake's scratch idle
    // When the next period starts
    // Then the scratch is freed now
    /// <summary>E9. Oracle: the scratch made whole first, held across the textures' period, and gone after the next.</summary>
    private static void FreedIdle(VkBackend vk, Checker check)
    {
        long mib = ScratchMib();
        check.Expect(_vkb.Scratched && mib >= FullScratchMiB, $"E9: the whole scratch took {mib} MiB, not {FullScratchMiB}");
        VramRung first = VramLadder.Period(GoldPast, GoldBudget);
        check.Expect(first == VramRung.Textures && _vkb.Scratched, $"E9: the first period past 90% gave {first} with the scratch held {_vkb.Scratched}");
        VramLadder.Period(GoldPast, GoldBudget);
        check.Expect(!_vkb.Scratched, "E9: the period after the textures did not free the idle scratch");
    }

    // Given use under 80%
    // When the next bake opens a page
    // Then it takes the full scratch
    /// <summary>E10. Oracle: the scratch's MiB by hand -- at most 232 past 90%, then the whole 448 once use is under 80%.</summary>
    private static void FullUnder(VkBackend vk, Checker check)
    {
        VramLadder.Period(GoldPast, GoldBudget);
        VramLadder.Period(GoldPast, GoldBudget);
        long half = ScratchMib();
        check.Expect(half > 0 && half <= HalfScratchMiB, $"E10: past 90% the scratch took {half} MiB, not half");
        VkFreeScratch();
        VramLadder.Period(GoldUnder, GoldBudget);
        long full = ScratchMib();
        check.Expect(!VramLadder.HalfScratch && full >= FullScratchMiB, $"E10: under 80% the next page's scratch took {full} MiB, not the whole {FullScratchMiB}");
    }

    // Given shadows were lowered to fit VRAM
    // When an area's lightmaps bake
    // Then they bake with the cube size shadow.cubesize names, their keys match an unlowered session's, and the disk cache hits
    /// <summary>E7. Oracle: an unlowered bake of the same fixture into the same cache, and the cube size from before the lowering.</summary>
    private static void LoweredKeysHit(VkBackend vk, Checker check, ExtenderLog log, int cube)
    {
        string dir = GoldDir();
        try
        {
            ulong look = ShadowLook();
            byte[]? plain = FixtureBake(vk, log, false, dir, out _);
            VramLadder.Period(GoldPast, GoldBudget);
            VramLadder.Period(GoldPast, GoldBudget);
            VramLadder.AreaChanged(GoldPast, GoldBudget, 960);
            VramLadder.Period(GoldPast, GoldBudget);
            check.Expect(VramLadder.ShadowsLowered && CasterPass.CubeFaceSize * 2 == cube, $"E7: the climb did not lower the cubes from {cube}");
            check.Expect(BakeCubeSize == cube && ShadowLook() == look,
                         $"E7: lowered, the bake renders its cubes at {BakeCubeSize}, not {cube}, or its key moved");
            byte[]? lowered = FixtureBake(vk, log, false, dir, out int hits);
            check.Expect(plain != null && lowered != null && plain.AsSpan().SequenceEqual(lowered) && hits > 0,
                         $"E7: lowered, the fixture's pages {(plain == null || lowered == null ? "did not bake" : "differ")} or the cache hit {hits} times");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Given one area baked once with half the scratch and once with all of it
    // When the pages are compared
    // Then they match, and each session reads the other's cache files
    /// <summary>E13. Oracle: the fixture baked twice, half and whole, each into a cache of its own, then each read from the other's.</summary>
    private static void HalfMatchesFull(VkBackend vk, Checker check, ExtenderLog log)
    {
        string halfDir = GoldDir(), fullDir = GoldDir();
        try
        {
            byte[]? half = FixtureBake(vk, log, true, halfDir, out _);
            byte[]? full = FixtureBake(vk, log, false, fullDir, out _);
            check.Expect(half != null && full != null && half.AsSpan().SequenceEqual(full),
                         $"E13: the half and whole scratch's pages {(half == null || full == null ? "did not bake" : "differ")}");
            FixtureBake(vk, log, false, halfDir, out int fullReadsHalf);
            FixtureBake(vk, log, true, fullDir, out int halfReadsFull);
            check.Expect(fullReadsHalf > 0 && halfReadsFull > 0,
                         $"E13: the whole bake read the half one's cache {fullReadsHalf} times and the half the whole's {halfReadsFull}");
        }
        finally { Directory.Delete(halfDir, true); Directory.Delete(fullDir, true); }
    }

    private static string GoldDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "cx-lm-vram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

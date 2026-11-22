using System;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;
using Ladder = CX.Alloy.VramLadderApprovedExamples;

namespace CX.Alloy;

/// <summary>Found tests of the VRAM ladder on the bake's Vulkan path: a scratch of the other size, the pool's page cap,
/// the textures rung's eviction and a waiting bake's retries. Oracles: the column counts and page counts by hand, a
/// texture landed at a hand-picked size, and the device's own count of refused allocations.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>The fake GL name the textures found test lands its idle texture under.</summary>
    private const uint FoundTexName = 0xE7000001u;   // not-an-offset: a fake texture name no device made

    /// <summary>MiB the textures found test lands its idle texture at.</summary>
    private const long FoundTexMiB = 3;

    /// <summary>A <c>lightmap.vram</c> of three whole pages.</summary>
    private const int FoundVramMib = 288;

    /// <summary>Every found test on <paramref name="vk"/>, with the bake's and the ladder's state put back after each.</summary>
    internal static void VramFound(VkBackend vk, Checker check, ExtenderLog log)
    {
        vk.DeviceLocalBytes(out long usage, out long budget, out _, out _);
        if (budget - usage < 1024 * GoldMiB) { check.Skip($"the bake's VRAM found tests need 1024 MiB free; the device has {(budget - usage) / GoldMiB} MiB"); return; }
        int cube0 = CasterPass.CubeFaceSize, dyn0 = CasterPass.CubeDynFaceSize;
        Guarded(vk, log, cube0, dyn0, () => WholeAfterHalf(check));
        Guarded(vk, log, cube0, dyn0, () => PoolTakesCap(check));
        Guarded(vk, log, cube0, dyn0, () => TexturesEvict(check, cube0, dyn0));
        Guarded(vk, log, cube0, dyn0, () => WaitAsksOnce(check, log));
    }

    /// <summary>Found: a half scratch still held when the ladder takes the half back is remade whole at the next page.
    /// Oracle: <see cref="PageSize"/> and <see cref="HalfCols"/> columns, by hand.</summary>
    private static void WholeAfterHalf(Checker check)
    {
        VramLadder.Period(Ladder.Past, Ladder.Budget);
        VramLadder.Period(Ladder.Past, Ladder.Budget);
        bool half = VkScratch();
        check.Expect(half && _vkb.Cols == HalfCols, $"found: past 90% the scratch is not the half one ({_vkb.Cols} columns, not {HalfCols})");
        VramLadder.Period(Ladder.Under, Ladder.Budget);
        bool whole = VkScratch();
        check.Expect(whole && _vkb.Cols == PageSize, $"found: under 80% the held half scratch was kept ({_vkb.Cols} columns, not {PageSize})");
    }

    /// <summary>Found: a pool made before the ladder capped it is made again at the cap.
    /// Oracle: 288 MiB is three 96 MiB pages, and the area change's room of 128 MiB is one.</summary>
    private static void PoolTakesCap(Checker check)
    {
        int vram = Lightmap.Vram.V;
        Lightmap.Vram.V = FoundVramMib;
        try
        {
            VkPool();
            check.Expect(_vkb.Layers == 3, $"found: precondition: a lightmap.vram of {FoundVramMib} made {_vkb.Layers} pages, not 3");
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.AreaChanged(Ladder.Past, Ladder.Budget, FoundVramMib);
            VkPool();
            check.Expect(_vkb.Layers == 1, $"found: the ladder capped the pool at {VramLadder.PoolCap} pages and it kept {_vkb.Layers}, not 1");
        }
        finally
        {
            if (_vkb.Layers > 0) VkFreePages();
            Revoke();
            Lightmap.Vram.V = vram;
        }
    }

    /// <summary>Found: the textures rung lets an idle texture go and its line says what that freed.
    /// Oracle: one texture landed at <see cref="FoundTexMiB"/> MiB by hand.</summary>
    private static void TexturesEvict(Checker check, int cube0, int dyn0)
    {
        const string resref = "cx_vramfound_t";
        var heard = new System.Collections.Generic.List<string>();
        var ear = new ExtenderLog { Tap = heard.Add };
        TexId id = TexRegistry.Acquire(resref);
        try
        {
            TexRegistry.Land(id, FoundTexName, FoundTexMiB * GoldMiB);
            TexRegistry.Release(id);
            check.Expect(TexRegistry.StateOf(id) == TexState.Idle, $"found: precondition: the released texture is {TexRegistry.StateOf(id)}, not idle");
            VramLadder.Reset(ear, cube0, dyn0);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            string? line = heard.Find(l => l.Contains(VramLadder.TexturesTag, StringComparison.Ordinal));
            long? freed = line == null ? null : AlloyRenderer.ClauseMiB(line, VramLadder.FreedKey);
            check.Expect(TexRegistry.StateOf(id) != TexState.Idle && freed >= FoundTexMiB,
                         $"found: past 90% the textures rung left the idle texture {TexRegistry.StateOf(id)} and freed {freed?.ToString() ?? "no"} MiB, not at least {FoundTexMiB}");
        }
        finally { TexRegistry.Forget(resref); }
    }

    /// <summary>Found: a waiting bake asks the device for its scratch once, not again in the same frame and period.
    /// Oracle: the device's count of refused allocations, unmoved by the second ask.</summary>
    private static void WaitAsksOnce(Checker check, ExtenderLog log)
    {
        VkBackend? capped = VkBackend.TryCreate(log, capMib: 512);
        check.Expect(capped != null, "found: the 512 MiB capped device was refused (logged above)");
        if (capped == null) return;
        BakeVkState mine = _vkb;
        _vkb = new BakeVkState { Dev = capped };
        BufferId ballast = capped.CreateBuffer(new BufferDesc(400 * GoldMiB, BufferUse.Storage, MemoryKind.Device, "golden vram ballast"));
        try
        {
            check.Expect(!ballast.IsNull, "found: the 400 MiB ballast did not fit under the 512 MiB cap");
            if (ballast.IsNull) return;
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VkScratch();
            int faults = capped.Faults;
            check.Expect(ScratchWaits, "found: precondition: with no room for half the scratch the bake did not wait");
            bool went = VkScratch();
            check.Expect(!went && capped.Faults == faults,
                         $"found: the waiting bake asked again in the same frame and period ({capped.Faults - faults} more refusals, went {went})");
        }
        finally
        {
            if (!ballast.IsNull) capped.Destroy(ballast);
            _bakePage = -1;
            VkFreeScratch();
            _vkb = mine;
            capped.Dispose();
        }
    }
}

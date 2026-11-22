using System;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The rungs Alloy gives way by as VRAM fills, in the order it takes them.</summary>
internal enum VramRung { None, Textures, Scratch, Pool, Shadows }

/// <summary>The pressure ladder: past 90% of the budget a period gives the next rung -- idle textures, the bake's
/// scratch, the lightmap pool at an area change, shadow resolution -- and under 80% an area change takes it back.</summary>
internal static class VramLadder
{
    /// <summary>The marker of the line a period that evicted idle textures gives, once per area.</summary>
    internal const string TexturesTag = "[vram] rung textures";

    /// <summary>The key the textures line gives what the eviction freed under, as <c>freed N MiB</c>.</summary>
    internal const string FreedKey = "freed";

    /// <summary>The marker of the line that says shadow quality was lowered to fit VRAM, once per lowering.</summary>
    internal const string ShadowsTag = "[vram] rung shadows";

    /// <summary>What a shadow size row in <c>/settings</c> says while the ladder draws it below its value.</summary>
    internal const string LoweredNote = "lowered to fit VRAM";

    /// <summary>The smallest cube size the shadow rung halves: at or under it the rung is passed over.</summary>
    internal const int SmallestLowered = 256;

    /// <summary>What the ladder has given this session, and the shadow sizes it was armed with.</summary>
    private sealed class LadderState
    {
        public ExtenderLog? Log;
        public int CubeSize, DynSize;
        public VramRung Given;
        public bool Lowered, Half, TexturesSaid;
        /// <summary>The pages the pool step left room for; -1 while the pool is not given.</summary>
        public int Pages = -1;
        /// <summary>Periods whose use was not past 90%.</summary>
        public int Eased;
    }

    private static LadderState _s = new();

    /// <summary>Arm the ladder afresh with nothing given: a new device, with the cube and moving-caster sizes
    /// <c>shadow.cubesize</c> and <c>shadow.dynsize</c> named at boot. A lowering still held is taken back first.</summary>
    internal static void Reset(ExtenderLog? log, int cubeSize, int dynSize)
    {
        if (_s.Lowered) CasterPass.DrawCubesAt(_s.CubeSize, _s.DynSize);
        _s = new LadderState { Log = log, CubeSize = cubeSize, DynSize = dynSize };
    }

    private static bool PastGive(long useMib, long budgetMib) => budgetMib > 0 && useMib * 10 > budgetMib * 9;
    private static bool UnderTake(long useMib, long budgetMib) => budgetMib > 0 && useMib * 10 < budgetMib * 8;

    /// <summary>A period's device figures, MiB: the rung this period gave, or <see cref="VramRung.None"/>.</summary>
    internal static VramRung Period(long useMib, long budgetMib)
    {
        LadderState s = _s;
        if (!PastGive(useMib, budgetMib)) s.Eased++;
        if (UnderTake(useMib, budgetMib)) { s.Half = false; return VramRung.None; }
        if (!PastGive(useMib, budgetMib)) return VramRung.None;
        long freed = TexRegistry.EvictIdle();
        if (!s.TexturesSaid)
        {
            s.TexturesSaid = true;
            s.Log?.Info($"{TexturesTag}: VRAM use {useMib} of {budgetMib} MiB is past 90%, idle textures evicted, {FreedKey} {freed >> 20} MiB");
        }
        if (s.Given >= VramRung.Scratch) s.Half = true;
        switch (s.Given)
        {
            case VramRung.None:
                s.Given = VramRung.Textures;
                return VramRung.Textures;
            case VramRung.Textures:
                s.Given = VramRung.Scratch;
                s.Half = true;
                LightmapBake.FreeIdleScratch();
                s.Log?.Info($"[vram] rung scratch: VRAM use {useMib} of {budgetMib} MiB is still past 90%, the lightmap bake takes half its scratch");
                return VramRung.Scratch;
            case VramRung.Pool when s.CubeSize > SmallestLowered:
                s.Given = VramRung.Shadows;
                s.Lowered = true;
                CasterPass.DrawCubesAt(Draws(s.CubeSize), Draws(s.DynSize));
                s.Log?.Info($"{ShadowsTag}: VRAM use {useMib} of {budgetMib} MiB is still past 90%, shadow quality was lowered to fit VRAM -- "
                            + $"light cubes {s.CubeSize} to {Draws(s.CubeSize)}, moving-object cubes {s.DynSize} to {Draws(s.DynSize)}");
                return VramRung.Shadows;
            default:
                return VramRung.None;
        }
    }

    /// <summary>The player changed area, with the device's figures at the change and the pool's share of the use, MiB.</summary>
    internal static void AreaChanged(long useMib, long budgetMib, long poolMib)
    {
        LadderState s = _s;
        s.TexturesSaid = false;
        if (UnderTake(useMib, budgetMib)) { TakeBack(s); return; }
        if (!PastGive(useMib, budgetMib) || s.Given < VramRung.Scratch) return;
        long room = budgetMib * 9 / 10 - (useMib - poolMib);
        int fit = (int)Math.Clamp(room / LightmapBake.PageMiB, 1, LightmapBake.MaxPages);
        s.Pages = s.Pages < 0 ? fit : Math.Min(s.Pages, fit);
        if (s.Given == VramRung.Scratch) s.Given = VramRung.Pool;
        s.Log?.Info($"[vram] rung pool: VRAM use {useMib} of {budgetMib} MiB is past 90% at an area change, the lightmap pool takes at most {s.Pages} pages");
    }

    /// <summary>Everything given goes back: the named shadow sizes, the whole scratch, the pool's own size.</summary>
    private static void TakeBack(LadderState s)
    {
        if (s.Lowered) CasterPass.DrawCubesAt(s.CubeSize, s.DynSize);
        if (s.Given != VramRung.None) s.Log?.Info($"[vram] taken back: VRAM use is under 80% at an area change, nothing gives way now");
        s.Lowered = false; s.Half = false; s.Pages = -1; s.Given = VramRung.None;
    }

    /// <summary>The furthest rung given and not yet taken back.</summary>
    internal static VramRung Given => _s.Given;

    /// <summary>The shadow arrays draw below the sizes their settings name.</summary>
    internal static bool ShadowsLowered => _s.Lowered;

    /// <summary>The light cube size <c>shadow.cubesize</c> names, which the arrays draw at unless lowered.</summary>
    internal static int CubeNamed => _s.Lowered ? _s.CubeSize : CasterPass.CubeFaceSize;

    /// <summary>The size a shadow array its setting names at <paramref name="named"/> draws at now.</summary>
    internal static int Draws(int named) => _s.Lowered ? named / 2 : named;

    /// <summary>Periods since the ladder was armed whose use was not past 90%: a waiter that sees it move tries again.</summary>
    internal static int Eased => _s.Eased;

    /// <summary>The bake's next scratch is the half one.</summary>
    internal static bool HalfScratch => _s.Half;

    /// <summary>The most pages the pool step leaves the lightmap pool; -1 while the pool is not given.</summary>
    internal static int PoolCap => _s.Pages;

    /// <summary>Pages the lightmap pool takes now for a <c>lightmap.vram</c> of <paramref name="vramMib"/>.</summary>
    internal static int PoolPages(int vramMib)
    {
        int full = LightmapBake.PoolPages(vramMib, AlloyRenderer.VramTotalMib, AlloyRenderer.VramFreeMib);
        return _s.Pages < 0 ? full : Math.Min(full, _s.Pages);
    }
}

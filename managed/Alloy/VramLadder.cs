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

    /// <summary>What the ladder has given this session, and the shadow sizes it was armed with.</summary>
    private sealed class LadderState
    {
        public ExtenderLog? Log;
        public int CubeSize, DynSize;
    }

    private static LadderState _s = new();

    /// <summary>Arm the ladder afresh with nothing given: a new device, with the cube and moving-caster sizes
    /// <c>shadow.cubesize</c> and <c>shadow.dynsize</c> named at boot.</summary>
    internal static void Reset(ExtenderLog? log, int cubeSize, int dynSize)
        => _s = new LadderState { Log = log, CubeSize = cubeSize, DynSize = dynSize };

    /// <summary>A period's device figures, MiB: the rung this period gave, or <see cref="VramRung.None"/>.</summary>
    internal static VramRung Period(long useMib, long budgetMib) => VramRung.None;

    /// <summary>The player changed area, with the device's figures at the change and the pool's share of the use, MiB.</summary>
    internal static void AreaChanged(long useMib, long budgetMib, long poolMib) { }

    /// <summary>The furthest rung given and not yet taken back.</summary>
    internal static VramRung Given => VramRung.None;

    /// <summary>The shadow arrays draw below the sizes their settings name.</summary>
    internal static bool ShadowsLowered => false;

    /// <summary>The size a shadow array its setting names at <paramref name="named"/> draws at now.</summary>
    internal static int Draws(int named) => named;

    /// <summary>The bake's next scratch is the half one.</summary>
    internal static bool HalfScratch => false;

    /// <summary>Pages the lightmap pool takes now for a <c>lightmap.vram</c> of <paramref name="vramMib"/>.</summary>
    internal static int PoolPages(int vramMib)
        => LightmapBake.PoolPages(vramMib, AlloyRenderer.VramTotalMib, AlloyRenderer.VramFreeMib);
}

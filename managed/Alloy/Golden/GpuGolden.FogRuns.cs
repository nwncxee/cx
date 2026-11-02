namespace CX.Alloy;

/// <summary>The froxel pass's own run decision, for the rain-only golden: the production predicate
/// <c>VkFogPass</c> gates on, with nothing restated here.</summary>
internal static partial class GpuGolden
{
    static partial void FogPassRuns(bool raining, bool vfogOn, ref int runs)
        => runs = FogVolume.Runs(raining, vfogOn) ? 1 : 0;
}

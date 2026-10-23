namespace CX.Alloy;

/// <summary>THE area edge: the world scene was destroyed (<c>DbScene</c>), so every pass's captured world and
/// every per-area diagnostic budget goes with it. The scene database's own tables are keyed on handles and
/// content, so nothing in them needs invalidating here.
/// <para>See docs/owned-resource-lifetimes.md.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Cold -- once a transition -- so the line it writes may allocate.</summary>
    internal static void AreaEdge(string why)
    {
        // ORDER: the hold IS this edge. LightmapBake.Revoke runs inside RevokeAll, and on its own it would
        // forward the very send the hold is sitting on (DropHold) and clear the edge marks the static set
        // closes against -- so the drop stands down across the revoke and the edge is re-armed after it,
        // leaving the baseline bake to start from the new area's inputs rather than the old area's.
        LightmapBake.BeginAreaEdge();
        RevokeAll(why);                    // every pass's captured world
        InvalidateRecords();               // ...and the per-slot draw-arg range cache (_cIdx/_cOff/_cBase)
        FrameDiffAreaEdge();               // the diff's pass count is per AREA, not per session
        CreatureParityAreaEdge();          // ...and so is the creature dump's
        DbBlackProbeAreaEdge();            // ...and so is the black probe's budget
        LightmapBake.EndAreaEdge();
        _log?.Info($"AlloyRenderer: [areaedge] {why} -- every persistent record unstamped. "
                 + "Kept (content-keyed): the texture cache, the bake blob cache, the pool tables");
    }
}

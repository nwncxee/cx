// CX-188: one pixel's shadow RECORD -- which shadow.perpixel lights shadow it, and each one's filtered visibility.
// cx_defshadow_cs writes it per record texel. Kept in a library of its own so a second reader can share the exact
// ranking; cx_deferred_cs used to, for a pixel the record missed, and that fill cost the walk 0.9 ms (2026-10-08).
// Composed after cx_lightfrag (CxPointLight, CxLightRank, the CxList* source) and cx_localshadow.
#ifndef CX_SHPICK
#define CX_SHPICK

/// The candidates, strongest CONTRIBUTION (CxLightRank) first, cut to the budget. <paramref name="ks"/> = the
/// kept slots (-1 = none or past the budget), <paramref name="kn"/> their raw N.L, <paramref name="kw"/> each
/// lane's CxShFade weight, <paramref name="rDrop"/> the budget's edge score (0 = nothing dropped) and the return
/// value the lights the budget left unshadowed (0 with no budget: those take a hardware tap instead).
uint CxShRank(vec3 worldPos, vec3 N, float viewDepth, float baked,
              out ivec4 ks, out vec4 kn, out vec4 kw, out float rDrop)
{
    uint cl = CxCluster(viewDepth);
    // A baked receiver's candidates are all in the LIVE prefix: every static light it could read is in the
    // lightmap pages (the walk's `inPages`), and the pages, the mirror tap and the hot-light debt own those
    // three cases between them -- none of them reaches the walk's shadow source. An unbaked one reads its
    // statics live, so it walks the whole kept list.
    uint n = baked > 0.5 ? CxListLive(cl) : CxListWalk(cl);
    // Named lanes, never an array indexed by a runtime variable -- that is what a shader compiler spills to
    // scratch memory (#336).
    vec4 kf = vec4(CX_RANK_NONE); ks = ivec4(-1); kn = vec4(0.0);
    // CX-188 budget: how many of those lanes shadow.perpixel lets this pixel keep. Lanes past it are cleared
    // below and the walk lights their lights UNSHADOWED -- a budget on what casts a shadow at all.
    int kN = CxShadowBudget(CX_SH_MAX);
    uint cand = 0u;
    // the best score that did NOT fit the four lanes -- the edge the lanes dissolve toward (CxShFade). Fed by
    // every value the chain below shifts out of kf.w.
    float k5 = 0.0;
    for (uint k = 0u; k < n; k++)
    {
        uint li = CxListIdx(cl, k);
        vec4 pr = uLights[li * 2u];
        vec4 cs = uLights[li * 2u + 1u];
        int w = int(cs.w);
        int slot = CxLightSlot(w);
        // slot -1 casts no shadow this frame and -2 is the ambient class, which takes none
        if (slot < 0) continue;
        if (CxLightIsStatic(w) && baked > 0.5) continue;   // inPages, as above
        vec3 c, L; float att, f2, ndlRaw, ndl;
        if (!CxPointLight(pr, cs.rgb, worldPos, N, c, L, att, f2, ndlRaw, ndl)) continue;
        if (ndl <= 0.0) continue;   // facing away: the walk pays for no cube fetch there either
        cand++;
        float rank = CxLightRank(att, c, f2);
        if (rank > kf.x)
        {
            k5 = max(k5, kf.w);
            kf = vec4(rank, kf.xyz); ks = ivec4(slot, ks.xyz); kn = vec4(ndlRaw, kn.xyz);
        }
        else if (rank > kf.y)
        {
            k5 = max(k5, kf.w);
            kf = vec4(kf.x, rank, kf.yz); ks = ivec4(ks.x, slot, ks.yz); kn = vec4(kn.x, ndlRaw, kn.yz);
        }
        else if (rank > kf.z)
        {
            k5 = max(k5, kf.w);
            kf = vec4(kf.xy, rank, kf.z); ks = ivec4(ks.xy, slot, ks.z); kn = vec4(kn.xy, ndlRaw, kn.z);
        }
        else if (rank > kf.w)
        {
            k5 = max(k5, kf.w);
            kf.w = rank; ks.w = slot; kn.w = ndlRaw;
        }
        else k5 = max(k5, rank);
    }
    if (kN < 4) ks.w = -1;
    if (kN < 3) ks.z = -1;
    if (kN < 2) ks.y = -1;
    if (kN < 1) ks.x = -1;
    // The budget's EDGE, and the per-lane dissolve toward it (cx_shrec's CxShFade). kN is uniform-derived, so
    // this chain is coherent across the dispatch and costs no read of kf indexed by a runtime variable.
    rDrop = uShBudget > 0.0 ? max(kN >= 4 ? k5 : kN == 3 ? kf.w : kN == 2 ? kf.z : kN == 1 ? kf.y : kf.x, 0.0) : 0.0;
    kw = CxShFade(kf, rDrop);
    // ...counted only while there IS a budget: with uShBudget 0 a light past the four lanes takes the walk's
    // hardware tap, which is not a light the budget left unshadowed.
    return uShBudget > 0.0 && cand > uint(kN) ? cand - uint(kN) : 0u;
}

/// The kept lanes' visibilities through the cheap filter (CxLocalShadowFast), the blocker search for the
/// strongest alone, with the budget's dissolve folded in. <paramref name="rot"/> is the disc's rotation in turns.
vec4 CxShTapRecord(ivec4 ks, vec4 kn, vec4 kw, float rDrop, vec3 worldPos, vec3 N, float onMover, float viewDepth,
                   float rot)
{
    // a lane the dissolve has already taken to nothing is not filtered at all
    vec4 vis;
    vis.x = ks.x < 0 || kw.x <= 0.0 ? 1.0 : CxLocalShadowFast(ks.x, worldPos, N, kn.x, onMover, viewDepth, rot, true);
    vis.y = ks.y < 0 || kw.y <= 0.0 ? 1.0 : CxLocalShadowFast(ks.y, worldPos, N, kn.y, onMover, viewDepth, rot, false);
    vis.z = ks.z < 0 || kw.z <= 0.0 ? 1.0 : CxLocalShadowFast(ks.z, worldPos, N, kn.z, onMover, viewDepth, rot, false);
    vis.w = ks.w < 0 || kw.w <= 0.0 ? 1.0 : CxLocalShadowFast(ks.w, worldPos, N, kn.w, onMover, viewDepth, rot, false);
    // the dissolve, folded into the record itself: skipped outright when nothing was dropped, so a frame with no
    // budget carries exactly the visibilities the filter returned
    return rDrop > 0.0 ? mix(vec4(1.0), vis, kw) : vis;
}

/// The disc rotation for a record at pixel/texel <paramref name="r"/>: four fixed rotations interleaved across a
/// 2x2 block, deterministic so it reads as a soft edge with TAA off. uLocalPcss.y is the taa stage's phase.
float CxShRot(ivec2 r) { return fract((float((r.x & 1) + 2 * (r.y & 1)) + 0.5) * 0.25 + uLocalPcss.y); }
#endif

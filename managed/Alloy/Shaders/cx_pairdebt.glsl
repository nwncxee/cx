// CX-188 step 4 (ADR 0029): the HOT-LIGHT DEBT, once per pixel. A creature standing under a baked static
// light owes the lightmap the shadow the bake never saw; the forward loop pays that per fragment per light
// (up to 16 segment-vs-sphere tests and then cube taps on the light's dyn page, which is the -10.1 ms
// `lightmap.movers 0` measures). The deferred stage has the hot branch compiled OUT (CX_NO_HOT), so this
// dispatch is where the debt comes from: PairShadows baked one small tile per (light, mover) PAIR, this pass
// culls those pairs against each 16x16 tile's own view-space box, and the pixels of a tile that holds one
// evaluate it and subtract.
// The subtraction is the spec's own line (docs/spec/deferred-lighting.md), on the G-buffer's lmShare lane:
//     hdr.rgb -= lmShare * min(lmSub.x / lmLum + lmSub.y, 1)
// with lmSub.x and lmSub.y the two quantities CxMoverOwe accumulates forward -- y the share of the pixel's
// own baked mover channel when the pair's light is in its pick, x a luminance estimate when it is not.
// Its own dispatch rather than a branch inside cx_deferred_cs for one reason: a tile with no pair over it
// exits at the first barrier having read NO G-buffer at all, and most tiles of a frame are that tile. Adding
// and subtracting into the HDR image commutes, so the order against the deferred pass does not matter --
// only that a memory barrier separates the two image passes.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

// The hot branch has no business in this stage either: the only static-light work here is the debt itself.
#define CX_NO_HOT 1
// ...and the pair library itself, which cx_localshadow brings in under this define: this stage's whole input.
#define CX_PAIR_RECV 1
/// This invocation's pixel CENTRE in window pixels -- the gl_FragCoord.xy of the fragment that wrote the
/// record, which is what the shadow dither is defined in terms of (CxLocalBakedVis spins its disc by it).
vec2 cxPixel;
#define CX_PIXEL cxPixel
#define CX_CLUSTER_XY ((cxPixel - uTile.xy) / uTile.zw)
#define CX_CLUSTER_FRAG

#include "cx_pass.glsl"
#include "cx_chartbuf.glsl"
#include "cx_gbuf.glsl"
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"

layout(binding = 3) uniform usampler2D uGb0;
layout(binding = 4) uniform usampler2D uGb1;
layout(binding = 5) uniform usampler2D uGb2;
/// The frame depth AFTER the cutout pass -- the surface this pass reconstructs and the record's ownership
/// test (cx_gbuf's CX_GB_RECORD), exactly as the deferred pass reads it.
layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
layout(rgba32ui, binding = 1) uniform uimage2D uLightRec;   // cx_gbuf's light record: this pass adds its debt

uniform ivec4 uRect;   // the render viewport: xy = origin in window pixels, zw = size
uniform mat4 uInvVp;   // inverse of the JITTERED view-projection the prepass rasterised with
uniform mat4 uProj;    // ...and that projection: the depth row linearises, the diagonal sizes a tile
uniform mat4 uView;    // world -> view, for the tile box; rigid, so its inverse is its transpose
/// /alloy viz 52: a pixel of a tile holding a pair shows how far its pairs' lookup got instead of the scene --
/// grey no volume, blue outside the tile, cyan an empty tile, yellow lit, red shadowed (magenta: no channel).
uniform int uDiag;

/// Pairs one tile may hold. A tile is 16x16 pixels of ONE surface slab, and a pair is one creature's shadow
/// from one lamp: four is already a crowd under four lamps. Overflow is counted, never silent.
const uint CX_PAIR_TILE_CAP = 32u;
/// Float slack on the tile's view-space box, both sides: cx_lightbuild's own, and for its reason -- the pair
/// volumes are measured in world space and the box is built in view space.
const float CX_PAIR_SLACK = 1e-3;

shared uint sPair[CX_PAIR_TILE_CAP];
shared uint sN, sDrop, sMinZ, sMaxZ;

/// [0] tiles whose pair list overflowed, [1] the pairs they dropped, [2] tiles that held at least one pair,
/// [3] the ablation ladder's sink, which nothing reads -- a bench variant's only way to keep the rung it
/// stopped at from being dead-stripped, and never written by the shipped program.
/// Loud by the period line, never silent. PER TILE and written by lane 0 alone: a counter incremented per
/// owing PIXEL would be a hundred thousand atomics a frame on one word, which is a cost to measure with and
/// not a measurement; tiles x the group's 256 pixels is the footprint either way.
layout(std430, binding = 14) buffer CxPairStats { uint uPairStats[]; };

/// This group's view-space box, from its padded NDC rect and the depth slab its own BAKED deferred pixels
/// span, plus the world-space bounding sphere the pair volumes are tested against. The rect is padded by one
/// pixel each way: the projection is JITTERED, which translates NDC by a sub-pixel amount this pass does not
/// undo. Same construction as cx_deferred_cs's own tile box -- the two passes bound the same tile.
void CxDebtTileBox(out vec3 fcw, out float fr)
{
    vec2 sz = vec2(uRect.zw);
    vec2 t0 = vec2(gl_WorkGroupID.xy * gl_WorkGroupSize.xy), t1 = min(t0 + vec2(gl_WorkGroupSize.xy), sz);
    vec2 pad = 2.0 / sz;
    vec2 n0 = 2.0 * t0 / sz - 1.0 - pad, n1 = 2.0 * t1 / sz - 1.0 + pad;
    float d0 = uintBitsToFloat(sMinZ) * (1.0 - CX_PAIR_SLACK), d1 = uintBitsToFloat(sMaxZ) * (1.0 + CX_PAIR_SLACK);
    vec3 lo = vec3(1e30), hi = vec3(-1e30);
    for (int c = 0; c < 8; c++)
    {
        float d = (c & 4) == 0 ? d0 : d1;
        vec3 p = vec3(((c & 1) == 0 ? n0.x : n1.x) * d / uProj[0].x,
                      ((c & 2) == 0 ? n0.y : n1.y) * d / uProj[1].y,
                      -d);
        lo = min(lo, p); hi = max(hi, p);
    }
    vec3 fc = 0.5 * (lo + hi);
    fr = 0.5 * length(hi - lo);
    fcw = transpose(mat3(uView)) * (fc - uView[3].xyz);
}

/// What ONE light's movers take back out of this pixel's baked term, in the two units <c>lmSub</c> carries --
/// x a luminance, y a fraction of the pixel's own mover channel. <c>CxMoverOwe</c>'s arithmetic, with the
/// light's dyn-page visibility replaced by <paramref name="vis"/>, the min over its pair tiles.
/// <paramref name="pi"/> is any pair of the light's run: they share its record.
vec2 CxDebtOwe(int pi, int slot, float vis, float dfade, vec3 worldPos, vec3 N, in CxGbuf g)
{
    if (vis >= 1.0 || slot < 0 || pi < 0 || pi >= uPairN) return vec2(0.0);
    CxPair p = uPair[pi];
    int ch = CxPickChan(int(p.ix.w), g.lmPick);
    // the bake's own answer: the share of the texel a mover in this light's path takes back
    if (ch < 4) return vec2(0.0, g.lmMover[ch] * dfade * uLocalFade[slot] * (1.0 - vis));
    // no channel (past the four a surface can hold): the live estimate, CxLocalDynShadowQ's formula with the
    // pair tiles standing in for the dyn page
    vec3 c, L; float att, f2, nr, nl;
    if (!CxPointLight(p.pr, p.col.rgb, worldPos, N, c, L, att, f2, nr, nl) || nl <= 0.0) return vec2(0.0);
    float lum = dot(CxPointDiffuse(c, att, nr, nl), vec3(0.2126, 0.7152, 0.0722));
    if (lum <= 0.0) return vec2(0.0);
    float bv = CxLocalBakedVis(slot, worldPos, N, nr, g.lmTexel, true);
    return vec2(lum * dfade * (1.0 - mix(1.0, 1.0 - bv * (1.0 - vis), uLocalFade[slot])), 0.0);
}

void main()
{
    // Every barrier below sits at the top level of main and runs for every invocation, however the tile's own
    // pixels turn out: a group where some lane had returned early would hang on the first one.
    uint lane = gl_LocalInvocationIndex;
    if (lane == 0u) { sMinZ = 0xffffffffu; sMaxZ = 0u; sN = 0u; sDrop = 0u; }
    barrier();

    ivec2 p = ivec2(gl_GlobalInvocationID.xy) + uRect.xy;
    cxPixel = vec2(p) + 0.5;
    float ndcZ = 1.0, viewDepth = 0.0;
    CxGbuf g;
    // lightmap.movers: 1 = a hot static light owes its mover's shadow; 0 = baked receivers skip static lights
    // outright, 2 = lightmap.movertaps 0, the diagnostic that stops at the ray test. Only 1 owes anything.
    bool mine = uLmMovers == 1 && all(lessThan(gl_GlobalInvocationID.xy, uvec2(uRect.zw)));
    float z = 1.0;
    if (mine)
    {
        z = texelFetch(uGbDepth, p, 0).r;
        ndcZ = z * 2.0 - 1.0;
        viewDepth = uProj[3].z / (ndcZ + uProj[2].z);
    }
    // The tile's depth slab is every DRAWN pixel's, off the depth buffer alone: the G-buffer is read below only
    // in a tile that holds a pair, which is most tiles not holding one. Wider than the baked pixels' own slab
    // (a pixel that owes nothing still widens it), so the cull is the conservative one.
    if (mine && z < 1.0) { atomicMin(sMinZ, floatBitsToUint(viewDepth)); atomicMax(sMaxZ, floatBitsToUint(viewDepth)); }
    barrier();

    // sMinZ > sMaxZ = no baked deferred pixel in this tile: nothing here can owe, and there is no box.
    // CX-188 ablation rung 1 (`/alloy bench`, CX.Alloy.AblateProgram): the G-buffer read and the tile's own
    // depth slab, with the pair cull itself absent -- every tile then holds no pair and owes nothing.
#if !defined(CX_ABLATE) || CX_ABLATE >= 2
    if (sMinZ <= sMaxZ && uPairN > 0)
    {
        vec3 fcw; float fr;
        CxDebtTileBox(fcw, fr);
        for (int i = int(lane); i < uPairN; i += int(gl_WorkGroupSize.x * gl_WorkGroupSize.y))
        {
            if (!CxPairReach(i, fcw, fr)) continue;
            uint at = atomicAdd(sN, 1u);
            if (at < CX_PAIR_TILE_CAP) sPair[at] = uint(i);
        }
    }
#endif
    barrier();
    if (lane == 0u)
    {
        sDrop = sN > CX_PAIR_TILE_CAP ? sN - CX_PAIR_TILE_CAP : 0u;
        sN = min(sN, CX_PAIR_TILE_CAP);
        // the cap is a budget WITH a counter, not a budget with a silence (the VAO drip's lesson)
        if (sDrop != 0u) { atomicAdd(uPairStats[0], 1u); atomicAdd(uPairStats[1], sDrop); }
        if (sN != 0u) atomicAdd(uPairStats[2], 1u);
        // ...and sorted ascending, which (the table being grouped by slot) puts one light's pairs together:
        // that is what lets a pixel fold two movers of one light into one min() instead of owing twice.
        // One lane, once per tile that holds a pair -- never per pixel.
        for (uint i = 1u; i < sN; i++)
        {
            uint v = sPair[i], j = i;
            while (j > 0u && sPair[j - 1u] > v) { sPair[j] = sPair[j - 1u]; j--; }
            sPair[j] = v;
        }
    }
    barrier();

#if defined(CX_ABLATE) && CX_ABLATE <= 2
    // Ablation rungs 1 and 2: the tile's own depth slab, and at rung 2 the pair cull and the sort as well,
    // with no G-buffer read, no pair tile tapped and nothing subtracted. The SINK is one atomic from lane 0
    // into the stats word nothing reads -- not a per-pixel image write, which neither rung would otherwise pay.
    // The full pass's own rung therefore carries the G-buffer read of a pair's tile along with the taps.
    if (lane == 0u) atomicAdd(uPairStats[3], sN + sMinZ);
    return;
#endif
    if (!mine || sN == 0u) return;
    if (!CxGbufRead(texelFetch(uGb0, p, 0), texelFetch(uGb1, p, 0), viewDepth, g)
        || (g.flags & CX_GB_BAKED) == 0u || !(g.lmLum > 0.0)) return;
    CxGbufFade(texelFetch(uGb2, p, 0).r, g);
    cxSurfFade = g.surfFade;
    vec4 h = uInvVp * vec4(2.0 * (cxPixel - vec2(uRect.xy)) / vec2(uRect.zw) - 1.0, ndcZ, 1.0);
    if (!(abs(h.w) > 1e-6)) return;
    vec3 worldPos = h.xyz / h.w;
    // CX_SLOT_NOSHADOW takes no local shadow at all, and shadow.cubedist fades the far end exactly as the
    // walk's own dfade does -- a pixel past it owes a mover nothing.
    float recv = (g.flags & CX_GB_NOSHADOW) != 0u ? 0.0 : 1.0;
    float dfade = (uLocalPcss.w > 0.0 ? 1.0 - smoothstep(uLocalPcss.w * 0.75, uLocalPcss.w, viewDepth) : 1.0) * recv;
    if (dfade <= 0.0) return;

    vec2 lmSub = vec2(0.0);
    int curSlot = -1, curPi = -1;
    float minVis = 1.0; int diagPi = -1;
    float curVis = 1.0, curNdl = 0.0;
    for (uint k = 0u; k <= sN; k++)
    {
        int pi = k < sN ? int(sPair[k]) : -1;
        int s = pi >= 0 ? int(uPair[pi].ix.z) : -1;
        if (s != curSlot)
        {
            lmSub += CxDebtOwe(curPi, curSlot, curVis, dfade, worldPos, g.N, g);
            curSlot = s; curPi = pi; curVis = 1.0;
            if (pi >= 0)
            {
                vec3 dl = uPair[pi].pr.xyz - worldPos;
                float dd = length(dl);
                curNdl = dd > 1e-4 ? dot(g.N, dl / dd) : 0.0;
            }
        }
        if (pi >= 0 && CxPairReach(pi, worldPos, 0.0))
        {
            curVis = min(curVis, CxPairTapAt(pi, worldPos, g.N, curNdl, true));
            if (curVis < minVis) { minVis = curVis; diagPi = pi; }
        }
    }
    // a baked shadow keeps uLocalFloor of its light (cx_bake_f), so a mover's takes back only the rest; the
    // channel shares in y were baked without the floor already (cx_lightfrag's own tail)
    lmSub.x *= 1.0 - uLocalFloor;
    float owe = min(lmSub.x / max(g.lmLum, 1e-5) + lmSub.y, 1.0);
    if (owe > 0.0)
    {
        uvec4 lr = imageLoad(uLightRec, p);
        vec2 dw = unpackHalf2x16(lr.w);
        if (dw.x != 0.0) imageStore(uLightRec, p, uvec4(lr.xyz, packHalf2x16(vec2(dw.x, min(dw.y + owe, 1.0)))));
    }
    if (uDiag != 0)
    {
        // the furthest stage any of the tile's pairs got to at this pixel
        int stage = 0; float wMin = 1.0;
        for (uint k = 0u; k < sN; k++)
        {
            int pi = int(sPair[k]);
            if (!CxPairReach(pi, worldPos, 0.0)) continue;
            stage = max(stage, 1);
            CxPair q = uPair[pi];
            vec4 c = q.vp * vec4(worldPos, 1.0);
            if (c.w <= 0.0) continue;
            vec3 n = c.xyz / c.w;
            if (any(greaterThanEqual(abs(n), vec3(1.0)))) continue;
            stage = max(stage, 2);
            wMin = min(wMin, q.fx.x);
            float r = n.z * 0.5 + 0.5;
            if (texture(uPairMap, vec4(n.xy * 0.5 + 0.5, q.ix.x, 1.0)) < 0.5) stage = max(stage, 3);   // any depth < 1 here
            if (texture(uPairMap, vec4(n.xy * 0.5 + 0.5, q.ix.x, r)) < 0.5) stage = 4;
        }
        int ch = diagPi >= 0 ? CxPickChan(int(uPair[diagPi].ix.w), g.lmPick) : 4;
        // grey: no pair's volume holds this point; blue: in a volume, outside its tile; cyan: in the tile, the
        // tile empty there; yellow: something drawn there but the point lit; red: shadowed (magenta: no channel)
        vec3 col = stage == 0 ? vec3(0.2) : stage == 1 ? vec3(0.0, 0.0, 1.0) : stage == 2 ? vec3(0.0, 1.0, 1.0)
                 : stage == 3 ? vec3(1.0, 1.0, 0.0) : ch < 4 ? vec3(1.0, 0.0, 0.0) : vec3(1.0, 0.0, 1.0);
        if (stage >= 2 && wMin < 0.5) col *= 0.3;   // the budget weight thinned it
        imageStore(uHdr, p, vec4(col, imageLoad(uHdr, p).a));
        return;
    }
    if (!(owe > 0.0)) return;
    vec4 hdr = imageLoad(uHdr, p);
    // alpha is the probe capture's 1 / distance, which this pass never runs for: hand it back untouched
    imageStore(uHdr, p, vec4(hdr.rgb - g.lmShare * owe, hdr.a));
}

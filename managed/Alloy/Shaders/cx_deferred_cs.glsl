// CX-188 (ADR 0029): the deferred point-light pass -- one invocation per render pixel of the main view. It reads
// the G-buffer the colour pass's CX_DEFERRED twin wrote (cx_gbuf), reconstructs the pixel's world position from
// the frame depth, and adds the point lights' diffuse, specular, film highlight and hot-light debt into the HDR
// target. The arithmetic IS the forward loop's: this stage composes cx_lightfrag and cx_material and calls
// CxClusteredLightLm and CxFilmStatics on the lanes the twin wrote, so there is no second copy of either to
// drift. docs/spec/deferred-lighting.md is the contract; cx_gbuf.glsl is the format.
// Two light LISTS, and light.deferredtiles is the A/B between them. OFF -- the default -- each pixel walks its
// own froxel's list, the forward loop's own input, which is what makes that mode the parity case. On, each group
// culls the area's lights once against the view-space slab between its own pixels' min and max depth, into a
// shared list every pixel of the tile then walks: a 16x16 tile is a small fraction of a froxel's screen area and
// its depth bound is the surface's own rather than a grid slice's, so the list is shorter than any froxel's.
// It is nonetheless the slower of the two here (-0.8 ms with it off): the cull re-walks every light of the area
// per group where the froxel table is built once for the whole grid. See light.deferredtiles' own declaration.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

// cx_material's material SYNTHESIS belongs to a fragment stage (the lit stage's slot constants, a screen
// derivative). The material here came off the G-buffer, already synthesised; the lobes are what this needs.
#define CX_NO_SYNTH 1
// ADR 0026: the light loop folds the material's own lobe in (CxLoopSpec), exactly as the lit stage does.
#define CX_LIT_MATERIAL 1
// ...and takes its light list from the CxList* this file defines, not from cx_lightfrag's froxel default.
#define CX_LIGHT_LIST 1
// CX-188 step 4: the walk's HOT branch is compiled OUT here. A baked receiver of this frame behaves toward a
// static light exactly as it does toward a cold one -- no 16-sphere ray test, no dyn-page taps -- and `lmSub`
// comes back zero. The debt those lights owe is taken once per pixel by cx_pairdebt, off the pair tiles
// CasterPass bakes in place of their pages. A macro and not a uniform because the registers the branch costs
// are charged to every pixel of the dispatch whether or not one is hot.
#define CX_NO_HOT 1
// ...and this stage IS where an unbaked receiver is lit -- a creature is opaque, so the twin drew it and this
// walk shades it -- so cx_localshadow's pair tap belongs here: a paired static light has no dyn page, and
// without the tap a creature would take no shadow from another creature under a lamp. Nowhere else, which is
// what keeps it out of the forward loop's registers.
#define CX_PAIR_RECV 1
// CX-188 step 3: ...and its shadows from the per-pixel visibility record cx_defshadow_cs wrote, not from the
// walk's own per-fragment cap. CxShadowFetch below is what the walk calls.
#define CX_SHADOW_SOURCE 1
// CX-188 budget: ...and the walk counts what it spent here -- lights lit and hardware taps paid, per pixel --
// so the period line can say the two numbers the budget is about instead of asserting them.
#define CX_WALK_STATS 1
// /alloy bench's PROBES (rungs 9..12): rung 6 -- no cube tap anywhere in the program -- plus ONE fetch that a
// single invocation takes: 9 the whole hardware tap, 10 the static cube array alone, 11 the dynamic one alone,
// 12 the pair atlas alone. Priced against rung 6 they say whether a tap costs per pixel or per program, and which
// texture carries a per-program cost.
#if defined(CX_ABLATE) && CX_ABLATE >= 9
#define CX_ONE_TAP (CX_ABLATE - 8)
#undef CX_ABLATE
#define CX_ABLATE 6
#endif

/// This invocation's pixel CENTRE in window pixels -- the gl_FragCoord.xy of the fragment that wrote the record,
/// which is what the froxel lookup and the shadow dither are both defined in terms of. Set first in main.
vec2 cxPixel;
#define CX_PIXEL cxPixel
// The froxel that fragment read, off the same viewport origin and in the same tile units. CX_CLUSTER_FRAG
// because the pixel does lie inside its own froxel -- which is what the build's bounds assume.
#define CX_CLUSTER_XY ((cxPixel - uTile.xy) / uTile.zw)
#define CX_CLUSTER_FRAG

#include "cx_pass.glsl"
#include "cx_ssr.glsl"
#include "cx_drawbuf.glsl"
#include "cx_chartbuf.glsl"
#include "cx_gbuf.glsl"
#include "cx_shrec.glsl"
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"
#include "cx_material.glsl"

layout(binding = 3) uniform usampler2D uGb0;
layout(binding = 4) uniform usampler2D uGb1;
layout(binding = 5) uniform usampler2D uGb2;
/// The frame depth AFTER the cutout pass -- the prepass plus any colour-pass write -- which is both the surface
/// this pass reconstructs and the record's ownership test (cx_gbuf's CX_GB_RECORD).
layout(binding = 7) uniform sampler2D uGbDepth;
/// CX-188 step 3: the visibility record (cx_shrec) -- the four verdicts and the surface key cx_defshadow_cs
/// wrote for the pixel, or for its 2x2 block at half resolution.
layout(binding = 8) uniform sampler2D uShVis;
layout(binding = 9) uniform usampler2D uShKey;
layout(rgba16f, binding = 0) uniform image2D uHdr;
#ifndef CX_FILM_PASS
layout(rgba32ui, binding = 1) uniform writeonly uimage2D uLightRec;   // cx_gbuf's light record, every pixel
#endif

uniform ivec4 uRect;   // the render viewport: xy = origin in window pixels, zw = size
uniform mat4 uInvVp;   // inverse of the JITTERED view-projection the prepass rasterised with
// ...and that projection itself: the depth row ([2].z, [3].z) linearises the buffer and the diagonal
// ([0].x, [1].y) sizes a tile in view space. Read off the matrix this pass was handed rather than from
// separately uploaded planes, which can drift from the frustum the depth was written with (cx_scenedepth
// takes the same care).
uniform mat4 uProj;
uniform mat4 uView;    // world -> view, for the tile cull's light positions; rigid, so its inverse is its transpose
uniform vec3 uEyePos;
uniform int uTiles;    // light.deferredtiles: 1 = this group's own culled list, 0 = each pixel's froxel list
uniform int uLightCount;
uniform int uReflK;    // R4 (weather.filmlights): cold statics to rank as film-reflection candidates; 0 = no film
uniform ivec2 uShSize; // the visibility record's size in texels
uniform int uShRes;    // shadow.deferredres: the record's divisor (1 full, 2 half); 0 = no record this frame

/// Lights a tile may hold -- the froxel cap, so one number sizes both list sources and CxFilmStatics' own
/// clamp against CX_CLUSTER_CAP still bounds the tiled walk. 512 bytes of shared memory at the shipped 128.
const uint CX_TILE_CAP = CX_CLUSTER_CAP;
/// R4's own ceiling on the film's reflection picks (cx_lightbuild CX_REFL_MAX).
const int CX_REFL_MAX = 4;
/// Float slack on the tile's view-space box, both sides: cx_lightbuild's own, and for its reason -- the walk
/// measures in world space and the cull in view space.
const float CX_TILE_SLACK = 1e-3;

shared uint sList[CX_TILE_CAP];
/// The list's two runs: [0, sLive) the live lights, [sLive, sLive + sRefl) R4's film picks at the head of the
/// cold statics, [sLive, sN) every cold static. Nothing is culled and every entry is a relevance candidate,
/// so liveCand = sLive and coldCand = kept = sN.
shared uint sLive, sN, sRefl;
/// Lights this tile could not hold, and the min/max of its DEFERRED pixels' linear view depth as float bits
/// (every depth here is positive, where the bit order is the float order).
shared uint sDrop, sMinZ, sMaxZ;
/// CX-188 budget: this tile's census -- lights lit, hardware taps paid, list ENTRIES touched, pixels shaded.
shared uint sWalk, sTaps, sEntry, sPix;
/// Lights -- or taps -- one uDefStats[4] / [5] count stands for. 24.5M lights a frame over a 64-frame harvest
/// would wrap a 32-bit counter outright; in sixteens it has three orders of headroom. Rounded rather than
/// truncated per group, so neither counter carries a systematic undercount, and a group's error is at most
/// half a unit against a 256-pixel denominator.
const uint CX_WALK_UNIT = 16u;
/// CX-188 step 3: this tile's deferred pixels that found no usable visibility record -- a half-resolution
/// texel whose own pixel was not deferred, or one no depth/normal weight accepted. They fall back to the
/// single hardware tap per light, which is a harder shadow rather than a missing one; counted all the same.
shared uint sMiss;

/// [0] tiles whose list overflowed, [1] the lights they dropped, [2] pixels with no usable shadow record,
/// [3] lights cx_defshadow_cs left unshadowed past shadow.perpixel's budget, [4] lights LIT, [5] hardware
/// shadow taps the walk still paid and [7] list ENTRIES the walk touched -- ALL THREE in units of
/// CX_WALK_UNIT, rounded per group, which is what keeps a 32-bit counter from wrapping between harvests --
/// and [6] deferred pixels shaded, the denominator of all three. [7] minus [4] is what the walk read and
/// threw away: the reach window's, the relevance cut's and the facing test's own rejects.
/// Averages for the period line, not an audit: the quotients resolve to about CX_WALK_UNIT / (pixels in a
/// group) = 1/16 of a light, a tap or an entry per pixel. Loud by the period line, never silent.
layout(std430, binding = 14) buffer CxDeferredStats { uint uDefStats[]; };

// ---- the list source ------------------------------------------------------------------------------------
// cx_lightfrag declares these; the walk reads nothing else. uTiles is a uniform, so the branch is coherent
// across the whole dispatch and the two modes share one program.
uint CxListN(uint cl) { return uTiles != 0 ? sN : CxClusterN(cl); }
uint CxListLive(uint cl) { return uTiles != 0 ? sLive : CxClusterLive(cl); }
uint CxListRefl(uint cl) { return uTiles != 0 ? sRefl : CxClusterRefl(cl); }
uint CxListLiveCand(uint cl) { return uTiles != 0 ? sLive : CxClusterLiveCand(cl); }
uint CxListColdCand(uint cl) { return uTiles != 0 ? sN : CxClusterColdCand(cl); }
uint CxListWalk(uint cl) { return uTiles != 0 ? sN : CxClusterWalk(cl); }
uint CxListIdx(uint cl, uint k)
{
    return uTiles != 0 ? sList[min(k, CX_TILE_CAP - 1u)] : uClIdx[cl * CX_CLUSTER_CAP + k];
}

// ---- the shadow source ----------------------------------------------------------------------------------
// CX-188 step 3: the pixel's four (slot, visibility) pairs, merged once in main and then read by the walk
// through CxShadowFetch. EIGHT scalars live across the loop, not the four gathered texels' thirty-six: the
// walk's register budget is the reason this pass exists at all, and a record that cost it back would be a
// strange way to spend the move.
/// The merged record: the four cube slots this pixel's shadows were evaluated for, -1 where no record texel
/// answered, and each one's visibility.
ivec4 cxShSlot = ivec4(-1);
vec4 cxShVis = vec4(1.0);
/// Any record at all answered here. False on a deferred pixel that was owed one is what the miss counter counts.
bool cxShHave = false;
/// The nearest record texel's slot set, whether or not its surface matched: what a missed pixel taps itself.
ivec4 cxShNear = ivec4(-1);

/// One record texel: its slots, its visibilities and its weight, 0 where it holds no record or holds another
/// surface's. <paramref name="w"/> is the bilinear share before the bilateral terms.
void CxShTap(ivec2 q, float w, float viewDepth, vec3 N, out ivec4 sl, out vec4 vi, out float wo)
{
    sl = ivec4(-1); vi = vec4(1.0); wo = 0.0;
    if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, uShSize))) return;
    uvec2 k = texelFetch(uShKey, q, 0).xy;
    float rd; vec3 rn;
    CxShUnpackKey(k.y, rd, rn);
    if (!(rd > 0.0)) return;   // no record was written there
    // set before the bilateral test: a rejected texel still names its lights for cxShNear
    sl = CxShUnpackSlots(k.x);
    float dw = 1.0 - min(abs(rd - viewDepth) / max(viewDepth * CX_SH_DEPTH, 1e-4), 1.0);
    wo = w * dw * smoothstep(CX_SH_NORMAL.x, CX_SH_NORMAL.y, dot(rn, N));
    if (wo <= 0.0) return;
    vi = texelFetch(uShVis, q, 0);
}

/// One texel folded into the pixel's own four lanes: its visibility for each slot <paramref name="want"/>
/// names, matched by INDEX and never by lane -- two texels whose light sets differ by one entry still answer
/// each other, which a plain bilinear blend of the lanes could not do.
void CxShFold(ivec4 sl, vec4 vi, float w, ivec4 want, inout vec4 num, inout vec4 den)
{
    if (w <= 0.0) return;
    vec4 m;
    m = vec4(equal(sl, ivec4(want.x))); num.x += w * dot(m, vi); den.x += w * dot(m, vec4(1.0));
    m = vec4(equal(sl, ivec4(want.y))); num.y += w * dot(m, vi); den.y += w * dot(m, vec4(1.0));
    m = vec4(equal(sl, ivec4(want.z))); num.z += w * dot(m, vi); den.z += w * dot(m, vec4(1.0));
    m = vec4(equal(sl, ivec4(want.w))); num.w += w * dot(m, vi); den.w += w * dot(m, vec4(1.0));
}

/// The pixel's record. Full resolution is its own texel and nothing else; half resolution reads the 2x2
/// neighbourhood, each texel weighted as if it sat at the CENTRE of its own 2x2 block of pixels -- so every
/// pixel sits three quarters along each axis toward its own texel and the weights are 9/16, 3/16, 3/16, 1/16.
/// That is also what mixes the four fixed disc rotations cx_defshadow_cs interleaves across a 2x2 block of
/// texels, so the record's cheap four-tap disc reads as sixteen tap positions a pixel rather than a pattern.
/// <para>The SLOT SET is the pixel's own texel's -- the nearest sample and the heaviest share -- and the
/// neighbours only supply their answer for those slots; where it holds no record the strongest neighbour's set
/// stands in. A light the neighbours hold and it does not is therefore left to the walk's own hardware tap,
/// which is the local answer and the conservative one.</para>
void CxShGather(ivec2 p, float viewDepth, vec3 N)
{
    if (uShRes <= 0) return;
    ivec4 s0, s1 = ivec4(-1), s2 = ivec4(-1), s3 = ivec4(-1);
    vec4 v0, v1 = vec4(1.0), v2 = vec4(1.0), v3 = vec4(1.0), w = vec4(0.0);
    if (uShRes == 1) CxShTap(p, 1.0, viewDepth, N, s0, v0, w.x);
    else
    {
        ivec2 q = p >> 1;
        ivec2 s = (p & 1) * 2 - 1;   // -1 toward the previous texel, +1 toward the next
        CxShTap(q,                 0.5625, viewDepth, N, s0, v0, w.x);
        CxShTap(q + ivec2(s.x, 0), 0.1875, viewDepth, N, s1, v1, w.y);
        CxShTap(q + ivec2(0, s.y), 0.1875, viewDepth, N, s2, v2, w.z);
        CxShTap(q + s,             0.0625, viewDepth, N, s3, v3, w.w);
    }
    ivec4 want = s0;
    if (w.x <= 0.0) want = w.y >= w.z && w.y >= w.w ? s1 : w.z >= w.w ? s2 : s3;
    vec4 num = vec4(0.0), den = vec4(0.0);
    CxShFold(s0, v0, w.x, want, num, den);
    CxShFold(s1, v1, w.y, want, num, den);
    CxShFold(s2, v2, w.z, want, num, den);
    CxShFold(s3, v3, w.w, want, num, den);
    cxShVis = num / max(den, vec4(1e-6));
    cxShSlot = want;
    // a lane nothing answered for is absent, so the walk takes its own tap there. An empty lane carries -1,
    // which no light the walk reaches this source with can be.
    if (den.x <= 0.0) cxShSlot.x = -1;
    if (den.y <= 0.0) cxShSlot.y = -1;
    if (den.z <= 0.0) cxShSlot.z = -1;
    if (den.w <= 0.0) cxShSlot.w = -1;
    cxShHave = any(greaterThan(w, vec4(0.0)));
    cxShNear = s0.x >= 0 ? s0 : s1.x >= 0 ? s1 : s2.x >= 0 ? s2 : s3;
}

/// A missed pixel's own verdict for <paramref name="slot"/>: the one hardware tap, no ranking and no disc.
float CxShHard(int slot, vec3 worldPos, vec3 N, float onMover, float viewDepth)
{
    if (slot < 0 || slot >= uLocalCubes) return 1.0;
    float ndlRaw = dot(N, normalize(uLocalLight[slot].xyz - worldPos));
    return CxLocalShadowQ(slot, worldPos, N, ndlRaw, onMover, viewDepth, false);
}

/// cx_lightfrag's shadow source: the UNFADED visibility of the light in <paramref name="slot"/>. The record
/// holds each slot at most once, so the matching lane IS the answer. `have` false = the record does not hold
/// this light, and the walk takes the single hardware tap itself.
float CxShadowFetch(int slot, out bool have)
{
    bvec4 eq = equal(cxShSlot, ivec4(slot));
    have = any(eq);
    return have ? dot(vec4(eq), cxShVis) : 1.0;
}

// ---- the tile cull --------------------------------------------------------------------------------------

/// cx_lightbuild's HotReach over this TILE's bounding sphere: could a baked static light in cube slot
/// <paramref name="slot"/> go hot anywhere in it -- could the walk's own per-fragment ray test pass for a point
/// inside? Every such point lies in the cone from the light tangent to the inflated mover sphere, so a sphere
/// that misses every cone holds no such point. Exact in effect rather than merely conservative: a pixel whose
/// ray test passes lies in both the cone and this sphere, so the intersection is non-empty and this says yes.
/// CX-188 step 4: compiled out with the hot branch itself -- no static light can be LIVE on a baked receiver
/// here any more, so the 16-sphere loop this ran per light per tile goes with it.
#ifndef CX_NO_HOT
bool CxTileHot(int slot, vec3 l, vec3 fc, float fr)
{
    if (slot < 0 || slot >= uLocalCubes || uLocalDynPage[slot & 63] < 0.5) return false;
    for (int q = 0; q < CX_DYN_SPHERES; q++)
    {
        vec4 ms = uDynMovers[slot * CX_DYN_SPHERES + q];
        if (ms.w < 0.0) break;   // CasterPass writes a slot's spheres from 0 up, unused ones after
        vec3 ax = ms.xyz - l;
        float rr = ms.w, d = length(ax);
        // the walk's test also passes a centre up to rr BEHIND the light along the ray, reachable only while
        // the light is within sqrt(2) rr of it: every direction, then
        if (d * d <= 2.0 * rr * rr) return true;
        ax /= d;
        float sn = rr / d, cs = sqrt(1.0 - sn * sn);
        vec3 v = fc - l;
        float along = dot(v, ax);
        if (along < -fr) continue;   // the tile is wholly behind the light
        float perp = sqrt(max(dot(v, v) - along * along, 0.0));
        if (cs * perp - along * sn <= fr) return true;
    }
    return false;
}
#endif

/// This group's view-space box, from its padded NDC rect and the depth slab its own pixels span, plus the
/// world-space bounding sphere <see cref="CxTileHot"/> tests against. The rect is padded by one pixel each
/// way: the projection is JITTERED, which translates NDC by a sub-pixel amount this pass does not undo.
void CxTileBox(out vec3 lo, out vec3 hi, out vec3 fcw, out float fr)
{
    vec2 sz = vec2(uRect.zw);
    vec2 t0 = vec2(gl_WorkGroupID.xy * gl_WorkGroupSize.xy), t1 = min(t0 + vec2(gl_WorkGroupSize.xy), sz);
    vec2 pad = 2.0 / sz;
    vec2 n0 = 2.0 * t0 / sz - 1.0 - pad, n1 = 2.0 * t1 / sz - 1.0 + pad;
    float d0 = uintBitsToFloat(sMinZ) * (1.0 - CX_TILE_SLACK), d1 = uintBitsToFloat(sMaxZ) * (1.0 + CX_TILE_SLACK);
    lo = vec3(1e30); hi = vec3(-1e30);
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

/// Does light <paramref name="i"/> reach this tile's box, and is it LIVE there? The radius is the light's own
/// SUPPORT -- past it CxPointLight returns false at every pixel of the tile, so the test drops nothing the
/// walk would have lit. Live = not a static, or a static the movers can make hot here (cx_lightbuild's rule,
/// over this tile instead of a froxel).
bool CxTileReaches(int i, vec3 lo, vec3 hi, vec3 fcw, float fr, bool hotOn, out bool live)
{
    live = false;
    vec4 pr = uLights[i * 2];
    // CX-188 budget: light.floor's own per-light cut as well, so the tile list is the window the walk will
    // apply (CxReachCut; 1e30 with the floor off, and then this is exactly the binreach radius as before).
    float r = pr.w * min(uReach > 0.0 ? min(uReach, 1.0) : 1.0,
                         sqrt(min(CxReachCut(CxLinear(uLights[i * 2 + 1].rgb)), 1.0)));
    vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
    vec3 dv = e - clamp(e, lo, hi);
    if (dot(dv, dv) > r * r) return false;
    int w = int(uLights[i * 2 + 1].w);
#ifdef CX_NO_HOT
    live = w < 128;   // CX-188 step 4: a static light is never live on a baked receiver of this stage
#else
    live = w < 128 || (hotOn && CxTileHot(CxLightSlot(w), pr.xyz, fcw, fr));
#endif
    return true;
}

/// R4: the cold statics a water film mirrors, as cx_lightbuild ranks them -- the K strongest by the engine's
/// curve at the box's nearest point times luma -- permuted to the front of the cold run so CxFilmStatics walks
/// [sLive, sLive + sRefl) and nothing else. One lane, and only on a frame that has a film at all.
void CxTileRefl(vec3 lo, vec3 hi)
{
    int K = min(uReflK, CX_REFL_MAX);
    float rs[CX_REFL_MAX]; uint rp[CX_REFL_MAX];
    uint refl = 0u;
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    for (uint k = sLive; k < sN; k++)
    {
        uint li = sList[k];
        vec4 pr = uLights[li * 2u];
        vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
        vec3 dv = e - clamp(e, lo, hi);
        float f2 = dot(dv, dv) / max(pr.w * pr.w, 1e-6);
        float s = (1.0 - f2) / (at.x + at.y * f2) * dot(uLights[li * 2u + 1u].rgb, vec3(0.299, 0.587, 0.114));
        uint j;
        if (refl < uint(K)) { j = refl; refl++; }
        else if (s > rs[(K - 1) & 3]) j = uint(K) - 1u;
        else continue;
        while (j > 0u && rs[(j - 1u) & 3u] < s) { rs[j & 3u] = rs[(j - 1u) & 3u]; rp[j & 3u] = rp[(j - 1u) & 3u]; j--; }
        rs[j & 3u] = s; rp[j & 3u] = k;
    }
    for (uint j = 0u; j < refl; j++)
    {
        uint src = rp[j & 3u], dst = sLive + j;
        if (src == dst) continue;
        uint t = sList[dst]; sList[dst] = sList[src]; sList[src] = t;
        // a later pick standing on the slot just claimed moved to where this one came from
        for (uint q = j + 1u; q < refl; q++) if (rp[q & 3u] == dst) rp[q & 3u] = src;
    }
    sRefl = refl;
}

void main()
{
    // Every barrier below sits at the top level of main and runs for every invocation, however the tile's own
    // pixels turn out: a group where some lane had returned early would hang on the first one.
    uint lane = gl_LocalInvocationIndex;
    if (lane == 0u)
    { sMinZ = 0xffffffffu; sMaxZ = 0u; sLive = 0u; sN = 0u; sRefl = 0u; sDrop = 0u; sMiss = 0u;
      sWalk = 0u; sTaps = 0u; sEntry = 0u; sPix = 0u; }
    barrier();

    ivec2 p = ivec2(gl_GlobalInvocationID.xy) + uRect.xy;
    cxPixel = vec2(p) + 0.5;
    float ndcZ = 1.0, viewDepth = 0.0;
    CxGbuf g;
    bool mine = all(lessThan(gl_GlobalInvocationID.xy, uvec2(uRect.zw)));
    bool inRect = mine;
    if (mine)
    {
        ndcZ = texelFetch(uGbDepth, p, 0).r * 2.0 - 1.0;
        // the forward stage's vDepth: gl_Position.w, which for a perspective projection IS the linear view depth
        viewDepth = uProj[3].z / (ndcZ + uProj[2].z);
        mine = CxGbufReadWalk(texelFetch(uGb0, p, 0), texelFetch(uGb1, p, 0).w, viewDepth, g);
    }
#ifndef CX_FILM_PASS
    // a zero where no walk will run (and on every ablation rung below the weights): last frame's record must never
    // read as this one's
#if !defined(CX_ABLATE) || CX_ABLATE >= 6
    if (inRect && !mine) imageStore(uLightRec, p, uvec4(0u));
#else
    if (inRect) imageStore(uLightRec, p, uvec4(0u));
#endif
#endif
    // the tile's depth slab is its OWN pixels' -- the one bound a froxel grid cannot have
    if (mine) { atomicMin(sMinZ, floatBitsToUint(viewDepth)); atomicMax(sMaxZ, floatBitsToUint(viewDepth)); }
    barrier();

    // sMinZ > sMaxZ = no deferred pixel in this tile, so there is nothing to cull a light against.
    bool tiled = uTiles != 0 && sMinZ <= sMaxZ;
    vec3 lo = vec3(0.0), hi = vec3(0.0), fcw = vec3(0.0);
    float fr = 0.0;
    // cx_lightbuild's own hot gate, from the pass block: the movers knob, and the tile's NEAR depth against
    // the view depth past which no cube is read (a pixel beyond it has dfade 0 and owes no mover anything).
    bool hotOn = false;
    if (tiled)
    {
        CxTileBox(lo, hi, fcw, fr);
#ifndef CX_NO_HOT
        hotOn = uLmMovers != 0 && !(uLocalPcss.w > 0.0 && uintBitsToFloat(sMinZ) >= uLocalPcss.w);
#endif
        // the live run first, so the two runs come out contiguous and in the order every walk expects
        for (int i = int(lane); i < uLightCount; i += int(gl_WorkGroupSize.x * gl_WorkGroupSize.y))
        {
            bool live;
            if (!CxTileReaches(i, lo, hi, fcw, fr, hotOn, live) || !live) continue;
            uint at = atomicAdd(sLive, 1u);
            if (at < CX_TILE_CAP) sList[at] = uint(i);
        }
    }
    barrier();
    if (lane == 0u)
    {
        sDrop = sLive > CX_TILE_CAP ? sLive - CX_TILE_CAP : 0u;
        sLive = min(sLive, CX_TILE_CAP);
        sN = sLive;
    }
    barrier();
    if (tiled)
        for (int i = int(lane); i < uLightCount; i += int(gl_WorkGroupSize.x * gl_WorkGroupSize.y))
        {
            bool live;
            if (!CxTileReaches(i, lo, hi, fcw, fr, hotOn, live) || live) continue;
            uint at = atomicAdd(sN, 1u);
            if (at < CX_TILE_CAP) sList[at] = uint(i);
        }
    barrier();
    if (lane == 0u)
    {
        sDrop += sN > CX_TILE_CAP ? sN - CX_TILE_CAP : 0u;
        sN = min(sN, CX_TILE_CAP);
        if (tiled && uReflK > 0 && sN > sLive) CxTileRefl(lo, hi);
        // the overflow is a budget with a counter, not a budget with a silence (the VAO drip's lesson)
#ifndef CX_FILM_PASS
        if (sDrop != 0u) { atomicAdd(uDefStats[0], 1u); atomicAdd(uDefStats[1], sDrop); }
#endif
    }
    barrier();

    // Not an early return: the record-miss reduce below is a barrier every invocation of the group reaches.
    uint miss = 0u;
    vec4 h = mine ? uInvVp * vec4(2.0 * (cxPixel - vec2(uRect.xy)) / vec2(uRect.zw) - 1.0, ndcZ, 1.0) : vec4(0.0);
    if (mine && abs(h.w) > 1e-6)
    {
        vec3 worldPos = h.xyz / h.w;
        vec3 V = normalize(uEyePos - worldPos);

        // The per-fragment material state CxLoopSpec and CxSpecLobe read, as the lit main set it before its first
        // light. cxSpecScale is rebuilt by the lit main's own CxSpecScaleOf, on this V; the sheen comes off the
        // draw's record by slot, through the lit main's own CxSheenOf.
        // g.rough is a unorm8 round-trip, so a fragment whose roughness sits within 1/255 of the light.hidden
        // ceiling can land on the other side of CxGlossy/CxGlossW here than it did forward. A lane's precision,
        // stated in the spec; not for this stage to paper over.
        cxSpecScale = CxSpecScaleOf(g.specC, max(dot(g.N, V), 0.0), g.rough, g.coat);
        cxF0 = g.specC; cxSpec = g.spec;
        cxCoat = g.coat; cxCoatRough = g.coatRough;
        CxSheenOf(uDraw[g.slot].ext1, cxSheen, cxSheenRough);
        cxFresnel = g.fresnel;
        cxModern = (g.flags & CX_GB_STOCKLOBE) != 0u ? 0.0 : 1.0;
        cxRecvShadow = (g.flags & CX_GB_NOSHADOW) != 0u ? 0.0 : 1.0;
        CxGbufFade(texelFetch(uGb2, p, 0).r, g);
        cxSurfFade = g.surfFade;

        float baked = (g.flags & CX_GB_BAKED) != 0u ? 1.0 : 0.0;
        float onMover = (g.flags & CX_GB_ONMOVER) != 0u ? 1.0 : 0.0;
#ifdef CX_FILM_PASS
        // R4, the film pass: the water film's highlights from the baked statics the walk skips. The gate is the
        // twin's (wxFilm > 0 && baked && cxCoat > 0), decided there and carried as a flag; every other pixel
        // leaves the target untouched.
        if ((g.flags & CX_GB_FILM) == 0u) mine = false;
        else
        {
            CxGbufWeights(texelFetch(uGb1, p, 0), g);
            vec3 add = g.specT * CxFilmStatics(worldPos, g.N, V, viewDepth, g.coat, g.coatRough, onMover);
#else
        // CX-188 ablation: the record fetch is rung 5 of the ladder (cx_lightfrag's CX_ABLATE), so the
        // stages below it run with the record's own defaults -- cxShVis 1, every slot -1.
#if !defined(CX_ABLATE) || CX_ABLATE >= 5
        // CX-188 step 3: the pixel's shadows, chosen and filtered once before the walk. A live record that
        // answers nothing here is a fallback to the hardware tap, which is counted rather than left silent.
        CxShGather(ivec2(gl_GlobalInvocationID.xy), viewDepth, g.N);
        // CX-188 budget: the walk's fallback switch. With a record, the record IS the pixel's shadow budget and
        // a light it does not hold is lit unshadowed; with none, every shadowing light takes the hardware tap,
        // as it did before the budget -- a pixel the record missed must not lose its shadows outright.
        cxShMiss = cxShHave ? 0.0 : 1.0;
        // the record pass's own gate (shadow.cubedist, the receiver flag, shadow.cap), so a pixel that was
        // never owed a record is not counted as having missed one
        if (uShRes > 0 && !cxShHave && uLocalSoft.w > 0.0 && cxRecvShadow > 0.0
            && (uLocalPcss.w <= 0.0 || viewDepth < uLocalPcss.w)) miss = 1u;
#if !defined(CX_SH_INLOOP) && (!defined(CX_ABLATE) || CX_ABLATE >= 7)
        // A pixel the record missed fills its own before the walk, so the walk never taps a cube in its loop: the
        // nearest texel's lights, one hardware tap each at this pixel. Borrowing that texel's answer instead (no
        // tap) only moved the cost onto the mirror tap below, 1.4 ms -- the first cube tap in this program pays.
        if (miss != 0u)
        {
            cxShSlot = cxShNear;
            cxShVis = vec4(CxShHard(cxShNear.x, worldPos, g.N, onMover, viewDepth),
                           CxShHard(cxShNear.y, worldPos, g.N, onMover, viewDepth),
                           CxShHard(cxShNear.z, worldPos, g.N, onMover, viewDepth),
                           CxShHard(cxShNear.w, worldPos, g.N, onMover, viewDepth));
            cxShHave = true;
            cxShMiss = 0.0;
        }
#endif
#endif
#ifdef CX_ONE_TAP
        if (all(equal(gl_GlobalInvocationID.xy, uvec2(0u))))
        {
            vec4 pc = vec4(worldPos - uLocalLight[0].xyz, float(max(cxShSlot.x, 0)));
#if CX_ONE_TAP == 1
            cxShVis.x *= CxLocalShadowQ(max(cxShSlot.x, 0), worldPos, g.N, 0.5, onMover, viewDepth, false);
#elif CX_ONE_TAP == 2
            cxShVis.x *= texture(uLocalShadow, pc, 0.5);
#elif CX_ONE_TAP == 3
            cxShVis.x *= texture(uLocalDynShadow, pc, 0.5);
#else
            cxShVis.x *= texture(uPairMap, vec4(fract(worldPos.xy), pc.w, 0.5));
#endif
        }
#endif
#if defined(CX_ABLATE) && CX_ABLATE == 1
        // Ablation rung 1: the G-buffer read, the record's own ownership test (CxGbufRead) and the world
        // position, and not one light. The SINK is what keeps them from being dead-stripped -- a tiny
        // multiple of every lane the rung produced, which the half-float target takes to zero.
        CxGbufWeights(texelFetch(uGb1, p, 0), g);
        vec3 add = vec3(dot(worldPos, vec3(1.0)) + dot(V, vec3(1.0)) + dot(g.N, vec3(1.0))
                        + g.rough + g.spec + g.specC + g.lmLum + g.lmTexel + cxSheenRough + float(g.flags)) * CX_ABL_EPS;
#else
        vec3 spec, lmSub;
        vec3 acc = CxClusteredLightLm(worldPos, g.N, V, viewDepth, onMover, g.rough, g.spec, baked,
                                      g.lmTexel, g.lmMover, g.lmPick, spec, lmSub);
#if !defined(CX_ABLATE) || CX_ABLATE >= 6
        // the weights are fetched again here, after the walk; the offset (0, since a view depth's sign bit is
        // clear) is one the compiler cannot prove, so it cannot merge this fetch with the top one and hold G1 live
        CxGbufWeights(texelFetch(uGb1, p + ivec2(int(sMaxZ >> 31u), 0), 0), g);
        // The exact linear correction the twin's weights were written for (docs/spec/deferred-lighting.md). The
        // film's own term, g.specT x CxFilmStatics, is the CX_FILM_PASS dispatch's: linear, so it adds exactly.
        float owe = min(lmSub.x / max(g.lmLum, 1e-5) + lmSub.y, 1.0);
        vec3 add = g.diffT * acc + g.specT * spec - g.lmShare * owe;
        // stored the moment it exists: carried to the end of main, its four words sat live across the whole walk
        // and the mirror tap's rung went from 0.0 to 2.2 ms (82b7b4e)
        imageStore(uLightRec, p, CxLightRecPack(acc, spec, viewDepth, baked > 0.5, owe));
#else
        // Ablation rungs 2..5: the walk ran as far as CX_ABLATE and its results go into the same sink, so
        // every rung pays the identical G-buffer read, world-position reconstruction and HDR read-modify-write
        // and the difference between two rungs is the stage between them. The weights' combine, the fallback
        // and mirror taps and the film are the later rungs' own work, so they are absent here.
        vec3 add = (acc + spec + lmSub + vec3(cxShVis.x + float(cxShSlot.x + 1))) * CX_ABL_EPS;
#endif
#endif
#endif
        vec4 hdr = imageLoad(uHdr, p);
        // alpha is the probe capture's 1 / distance, which this pass never runs for: hand it back untouched
        imageStore(uHdr, p, vec4(hdr.rgb + add, hdr.a));
#ifdef CX_FILM_PASS
        }
#endif
    }
    if (miss != 0u) atomicAdd(sMiss, miss);
#ifndef CX_FILM_PASS
    if (mine)
    {
        atomicAdd(sPix, 1u);
        if (cxWalkStats.x > 0.0) atomicAdd(sWalk, uint(cxWalkStats.x));
        if (cxWalkStats.y > 0.0) atomicAdd(sTaps, uint(cxWalkStats.y));
        if (cxWalkStats.z > 0.0) atomicAdd(sEntry, uint(cxWalkStats.z));
    }
#endif
    barrier();
    if (lane == 0u)
    {
        if (sMiss != 0u) atomicAdd(uDefStats[2], sMiss);
        uint walkU = (sWalk + CX_WALK_UNIT / 2u) / CX_WALK_UNIT, tapU = (sTaps + CX_WALK_UNIT / 2u) / CX_WALK_UNIT;
        uint entU = (sEntry + CX_WALK_UNIT / 2u) / CX_WALK_UNIT;
        if (walkU != 0u) atomicAdd(uDefStats[4], walkU);
        if (tapU != 0u) atomicAdd(uDefStats[5], tapU);
        if (sPix != 0u) atomicAdd(uDefStats[6], sPix);
        if (entU != 0u) atomicAdd(uDefStats[7], entU);
    }
}

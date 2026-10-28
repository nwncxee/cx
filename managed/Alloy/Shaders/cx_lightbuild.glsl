// The cluster build. One invocation per froxel: reconstruct the froxel's eye-space AABB from the
// projection diagonal (x = ndc.x * depth / P00), then a sphere-vs-AABB test per light. O(clusters *
// lights) with no CPU involvement -- 3456 * ~120 tests is nothing for the GPU and, unlike the engine's
// CPU prioritizer, its cost does not grow with draw count.
#version 460 core
layout(local_size_x = $GROUP$) in;
layout(std430, binding = 8) readonly buffer LightBuf { vec4 uLights[]; };
layout(std430, binding = 9) writeonly buffer ClusterCount { uint uClCount[]; };
layout(std430, binding = 10) buffer ClusterIdx { uint uClIdx[]; };
// [0] froxels that overflowed CAP; [1..3] a sampled build's entry totals -- binned, the kept prefix a
// fragment walks, and the floor candidates its light.relevance search reads
layout(std430, binding = 14) buffer Stats { uint uStats[]; };
// CasterPass.CubeDynSphere, the lit stage's own mover spheres (xyz, r; r < 0 unused), $DYNSPH$ per cube slot
layout(std430, binding = 11) readonly buffer DynMovers { vec4 uDynMovers[]; };
uniform mat4 uView;
uniform vec2 uProjXY;
uniform vec2 uZ;
uniform ivec3 uGrid;
uniform int uLightCount;
uniform float uReach;
// CX-188 budget: x = light.floor, y = the lit stage's master gain (uFalloff.x) -- the two terms of ReachCut
uniform vec2 uLightFloor;
uniform int uBinCap;
uniform vec2 uAtten;
// per cube slot: its dyn page plus one (0 = no mover near the light), as the lit stage's uLocalDynPage
uniform float uDynPage[$CUBES$];
// x = lightmap.movers (1 = a static light can go hot), y = shadow.cubedist (0 = no cut)
uniform vec2 uHot;
// R4 (weather.filmlights): how many cold statics to rank as reflection candidates for a water film.
// 0 = none, which is every frame with no film, and then this pass writes exactly what it always did.
uniform int uReflK;
// x = light.relevance's cut, the SAME snapshot the lit stage's uRelevance.x carries this frame (0 = no bounds);
// y = the lit stage's uReach, so Importance() is CxLightImportance term for term (0 = no reach fade)
uniform vec2 uRel;
// one pixel of this pass's viewport in NDC: the bounds' box is padded by it for the TAA jitter
uniform vec2 uPad;
// 1 = add this build's entry totals to uStats[1..3] (a sampled frame)
uniform int uTally;
const int CX_REFL_MAX = 4;
const uint CAP = $CAP$u;
const uint SORT = $SORT$u;
const int DYNSPH = $DYNSPH$;
const int CUBES = $CUBES$;
// Transient entry flags, stripped before the pass ends: a light index never reaches bit 29.
const uint FLAG_LIVE = 0x80000000u, FLAG_CULL = 0x40000000u, FLAG_CAND = 0x20000000u, IDX_MASK = 0x1fffffffu;
// float slack on both sides of the bounds: the lit stage measures in world space, this pass in view space
const float SLACK = 1e-3;

shared uint sTally[3];

// Could a baked static light in cube slot `slot` go HOT anywhere in this froxel -- could the lit stage's
// per-fragment ray test (cx_lightfrag: the light-to-fragment segment within a mover sphere, padded at publish) pass
// for a point in it? Every such point lies in the cone from the light tangent to the inflated sphere, so a
// froxel whose bounding sphere (fc, fr) misses every slot's cone cannot hold one. Conservative by design.
// WORLD space (l the light, fc the froxel centre): the spheres are, and a rigid view keeps every distance.
bool HotReach(int slot, vec3 l, vec3 fc, float fr)
{
    if (slot < 0 || slot >= CUBES || uDynPage[slot] < 0.5) return false;
    for (int q = 0; q < DYNSPH; q++)
    {
        vec4 ms = uDynMovers[slot * DYNSPH + q];
        if (ms.w < 0.0) break;   // CasterPass writes a slot's spheres from 0 up, unused ones after
        vec3 ax = ms.xyz - l;
        float rr = ms.w, d = length(ax);
        // the lit test also passes a centre up to rr BEHIND the light along the ray, reachable only while the
        // light is within sqrt(2) rr of it: every direction, then
        if (d * d <= 2.0 * rr * rr) return true;
        ax /= d;
        float sn = rr / d, cs = sqrt(1.0 - sn * sn);
        vec3 v = fc - l;
        float along = dot(v, ax);
        if (along < -fr) continue;   // the froxel is wholly behind the light
        float perp = sqrt(max(dot(v, v) - along * along, 0.0));
        if (cs * perp - along * sn <= fr) return true;
    }
    return false;
}

// CX-188 budget: cx_lightfrag's CxReachCut -- the f2 at which this light's own brightness falls under
// light.floor, and so where its binned radius ends. CONSERVATIVE on purpose: the build must never drop a light
// the walk would light, while a light it bins that the walk then windows out costs one list entry and nothing
// else. Two things make `lum` here an upper bound on the walk's:
//  - max(c, c*c*c) per channel, which is >= the walk's c^2.2 for EVERY c >= 0 (c >= c^2.2 below 1, c^3 >= c^2.2
//    above it). The plain sRGB value alone is not the bound: an overbright channel above 1 linearises UP, and
//    this pass must not have to trust that the walk already divided the colour down.
//  - uDayDim is not in it, and uDayDim is at most 1 (it is 1/the daylight ratio).
// 1e30 = no cut, so `min(uReach, sqrt(...))` is exactly uReach when the floor is off.
float ReachCut(vec3 col)
{
    if (uLightFloor.x <= 0.0) return 1e30;
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    vec3 c = max(col, vec3(0.0));
    float lum = uLightFloor.y * dot(max(c, c * c * c), vec3(0.2126, 0.7152, 0.0722));
    return clamp((lum - uLightFloor.x * at.x) / max(lum + uLightFloor.x * at.y, 1e-8), 0.0, 1.0);
}
/// The radius this build bins light `pr` by: the smaller of light.binreach's share of the authored radius and
/// light.floor's perceptual cut.
float ReachR(vec4 pr, vec3 col) { return pr.w * min(uReach, sqrt(ReachCut(col))); }

// cx_lightfrag's CxLightImportance at squared distance dd: non-increasing in dd, so the nearest point of a
// box bounds it from above over the box and the farthest point from below.
float Importance(vec4 pr, vec3 col, float dd, vec2 at)
{
    float f2 = dd / max(pr.w * pr.w, 1e-8);
    if (f2 >= 1.0) return 0.0;
    float fade = 1.0 - f2;
    if (uRel.y > 0.0)
    {
        float r2 = uRel.y * uRel.y;
        if (f2 > r2) return 0.0;
        fade *= min((r2 - f2) / (r2 * 0.3), 1.0);
    }
    return fade / (at.x + at.y * f2) * dot(col, vec3(0.299, 0.587, 0.114));
}

// cx_vanenv_f's CxVisionLight term for one light, also non-increasing in dd: that walk is a max with no
// relevance cut, so a culled light must not be able to win it either.
float Vision(vec4 pr, vec3 col, float dd, vec2 at)
{
    float rr = pr.w * pr.w;
    if (dd > rr) return 0.0;
    float f2 = dd / max(rr, 1e-8);
    return (1.0 - f2) / (at.x + at.y * f2) * dot(pow(max(col, vec3(0.0)), vec3($SRGB$)), vec3(0.299, 0.587, 0.114));
}

void Swap(uint a, uint b)
{
    uint t = uClIdx[a]; uClIdx[a] = uClIdx[b]; uClIdx[b] = t;
}

// Moves the entries of [a, b) carrying `flag` to its front; returns where they end.
uint Partition(uint base, uint a, uint b, uint flag)
{
    uint m = a;
    for (uint k = a; k < b; k++)
        if ((uClIdx[base + k] & flag) != 0u) { if (k != m) Swap(base + k, base + m); m++; }
    return m;
}

void Build(uint id, out uint binned, out uint keptOut, out uint candOut, out bool overflow)
{
    int gx = int(id) % uGrid.x;
    int gy = (int(id) / uGrid.x) % uGrid.y;
    int gz = int(id) / (uGrid.x * uGrid.y);

    float lz = log(uZ.y / uZ.x);
    float d0 = uZ.x * exp(lz * (float(gz) / float(uGrid.z)));
    float d1 = uZ.x * exp(lz * (float(gz + 1) / float(uGrid.z)));
    float x0 = -1.0 + 2.0 * float(gx) / float(uGrid.x), x1 = -1.0 + 2.0 * float(gx + 1) / float(uGrid.x);
    float y0 = -1.0 + 2.0 * float(gy) / float(uGrid.y), y1 = -1.0 + 2.0 * float(gy + 1) / float(uGrid.y);
    // The bounds' box: the froxel grown by the jitter pixel, and slice 0 from the eye (a fragment nearer than
    // uZ.x clamps into it). Binning keeps the exact box, so the lists themselves are what they were.
    float pd0 = gz == 0 ? 0.0 : d0 * (1.0 - SLACK), pd1 = d1 * (1.0 + SLACK);

    vec3 lo = vec3(1e30), hi = vec3(-1e30), plo = vec3(1e30), phi = vec3(-1e30);
    for (int c = 0; c < 8; c++)
    {
        float d = (c & 4) == 0 ? d0 : d1;
        vec3 p = vec3(((c & 1) == 0 ? x0 : x1) * d / uProjXY.x,
                      ((c & 2) == 0 ? y0 : y1) * d / uProjXY.y,
                      -d);
        lo = min(lo, p); hi = max(hi, p);
        float pd = (c & 4) == 0 ? pd0 : pd1;
        vec3 q = vec3(((c & 1) == 0 ? x0 - uPad.x : x1 + uPad.x) * pd / uProjXY.x,
                      ((c & 2) == 0 ? y0 - uPad.y : y1 + uPad.y) * pd / uProjXY.y,
                      -pd);
        plo = min(plo, q); phi = max(phi, q);
    }
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);

    // light.relevance's floor. The lit stage cuts a light whose importance is under uRel.x of the brightest
    // binned light AT THAT FRAGMENT; L is a value that brightest is never under anywhere in the box (the
    // best farthest-point importance), so a light whose NEAREST-point importance is <= uRel.x * L is cut at
    // every fragment here, and a light whose nearest point cannot reach L is never the brightest. Both are
    // per-fragment facts made per froxel: no cluster seam can come of either. Off with relevance off and under
    // /light bincap (the cap could evict the light L came from); a froxel past CAP (the same) keeps zero floors,
    // so it cuts only lights that light nothing there.
    bool bound = uRel.x > 0.0 && uRel.x < 1.0 && uBinCap <= 0;
    float floorI = 0.0, floorV = 0.0;
    if (bound)
    {
        uint nb = 0u;
        for (int i = 0; i < uLightCount; i++)
        {
            vec4 pr = uLights[i * 2];
            float r = ReachR(pr, uLights[i * 2 + 1].rgb);
            vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
            vec3 dv = e - clamp(e, lo, hi);
            if (dot(dv, dv) > r * r) continue;
            nb++;
            vec3 fv = max(abs(e - plo), abs(e - phi));
            float df = dot(fv, fv);
            vec3 col = uLights[i * 2 + 1].rgb;
            floorI = max(floorI, Importance(pr, col, df, at));
            floorV = max(floorV, Vision(pr, col, df, at));
        }
        if (nb > CAP) { floorI = 0.0; floorV = 0.0; }
        floorI *= 1.0 - SLACK; floorV *= 1.0 - SLACK;
    }

    // #336: walk order, unsorted. A per-froxel order for the receiver's tap cap put the cap's seam
    // on the cluster grid (screen-aligned squares wherever four cube lights overlapped); the cap
    // chooses per FRAGMENT now, so the list needs no order.
    uint n = 0u;
    uint base = id * CAP;
    uint dropped = 0u;
    // EXPERIMENT (/light bincap): keep only the K strongest, by the engine's curve at the froxel's
    // nearest point times luma -- the ordering the per-fragment body would find, made once per froxel.
    uint K = uBinCap <= 0 ? CAP : min(uint(uBinCap), SORT);
    float sc[SORT];
    vec3 fc = 0.5 * (lo + hi);
    float fr = 0.5 * length(hi - lo);
    vec3 fcw = transpose(mat3(uView)) * (fc - uView[3].xyz);   // uView is rigid: its inverse is the transpose
    // Cold statics: a static light is in the pages, and a baked matte receiver owes it nothing unless it goes
    // HOT -- which needs a cube with a dyn page, lightmap.movers, a froxel nearer than the cube distance and a
    // mover whose shadow cone reaches the froxel (HotReach). Everything else is LIVE.
    bool hotOn = uHot.x > 0.5 && !(uHot.y > 0.0 && d0 >= uHot.y);
    for (int i = 0; i < uLightCount; i++)
    {
        vec4 pr = uLights[i * 2];
        float r = ReachR(pr, uLights[i * 2 + 1].rgb);
        vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
        vec3 dv = e - clamp(e, lo, hi);
        float dd = dot(dv, dv);
        if (dd > r * r) continue;
        vec4 cs = uLights[i * 2 + 1];
        int w = int(cs.w);
        int set = (w - 128) / 512 - 1;
        bool live = w < 128 || hotOn && HotReach(w - 256 - 512 * (set + 1), pr.xyz, fcw, fr);
        uint entry = uint(i) | (live ? FLAG_LIVE : 0u) | FLAG_CAND;
        if (bound)
        {
            vec3 nv = e - clamp(e, plo, phi);
            float dn = dot(nv, nv);
            float u = Importance(pr, cs.rgb, dn, at) * (1.0 + SLACK);
            float uv = Vision(pr, cs.rgb, dn, at) * (1.0 + SLACK);
            if (u < floorI) entry &= ~FLAG_CAND;
            if (u <= uRel.x * floorI && (uv < floorV || uv <= 0.0)) entry |= FLAG_CULL;
        }
        if (uBinCap <= 0)
        {
            if (n >= CAP) { dropped = 1u; break; }
            uClIdx[base + n] = entry;
            n++;
            continue;
        }
        float f2 = dd / (pr.w * pr.w);
        float s = (1.0 - f2) / (at.x + at.y * f2) * dot(cs.rgb, vec3(0.299, 0.587, 0.114));
        uint j;
        if (n < K) j = n++;
        else if (s > sc[K - 1u]) { j = K - 1u; dropped = 1u; }
        else { dropped = 1u; continue; }
        while (j > 0u && sc[j - 1u] < s)
        {
            sc[j] = sc[j - 1u]; uClIdx[base + j] = uClIdx[base + j - 1u];
            j--;
        }
        sc[j] = s; uClIdx[base + j] = entry;
    }

    // The layout every walk reads (cx_lightfrag CxCluster*): [0, liveCand) live floor candidates,
    // [liveCand, live) the rest of the live lights, [live, live + refl) R4's picks, [.., coldCand) cold floor
    // candidates, [.., kept) the other cold statics, [kept, n) the culled. A fragment walks [0, kept) at most;
    // a vertex stage's position can lie outside its froxel, so it walks [0, n) and the bounds owe it nothing.
    uint live = 0u, kept = n;
    for (uint mid = 0u; mid < kept;)
    {
        uint e = uClIdx[base + mid];
        if ((e & FLAG_CULL) != 0u) { kept--; Swap(base + mid, base + kept); }
        else if ((e & FLAG_LIVE) != 0u) { if (mid != live) Swap(base + mid, base + live); live++; mid++; }
        else mid++;
    }
    uint liveCand = Partition(base, 0u, live, FLAG_CAND);
    // R4: the reflection candidates -- the K cold statics with the strongest curve x luma at this froxel,
    // culled or not (the film walk has no relevance cut), moved to the front of the cold run so the lit stage
    // can walk [live, live + refl) and nothing else.
    uint refl = 0u;
    if (uReflK > 0)
    {
        int K = min(uReflK, CX_REFL_MAX);
        float rs[CX_REFL_MAX]; uint rp[CX_REFL_MAX];
        for (uint k = live; k < n; k++)
        {
            uint li = uClIdx[base + k];
            if ((li & FLAG_LIVE) != 0u) continue;
            li &= IDX_MASK;
            vec4 pr = uLights[li * 2u];
            vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
            vec3 dv = e - clamp(e, lo, hi);
            float f2 = dot(dv, dv) / max(pr.w * pr.w, 1e-6);
            float s = (1.0 - f2) / (at.x + at.y * f2) * dot(uLights[li * 2u + 1u].rgb, vec3(0.299, 0.587, 0.114));
            uint j;
            if (refl < uint(K)) { j = refl; refl++; }
            else if (s > rs[K - 1]) j = uint(K) - 1u;
            else continue;
            while (j > 0u && rs[j - 1u] < s) { rs[j] = rs[j - 1u]; rp[j] = rp[j - 1u]; j--; }
            rs[j] = s; rp[j] = k;
        }
        for (uint j = 0u; j < refl; j++)
        {
            uint src = rp[j];
            if (src >= kept)
            {   // a culled pick joins the kept run at its end; the culled entry it displaces takes its place
                Swap(base + src, base + kept);
                for (uint q = j + 1u; q < refl; q++) if (rp[q] == kept) rp[q] = src;
                src = kept++;
            }
            uint dst = live + j;
            if (src == dst) continue;
            Swap(base + dst, base + src);
            // a later candidate standing on the slot just claimed moved to where this one came from
            for (uint q = j + 1u; q < refl; q++) if (rp[q] == dst) rp[q] = src;
        }
    }
    uint coldCand = Partition(base, live + refl, kept, FLAG_CAND);
    for (uint k = 0u; k < n; k++) uClIdx[base + k] &= IDX_MASK;

    uClCount[id * 3u] = n | (live << 16u);
    uClCount[id * 3u + 1u] = refl | (liveCand << 16u);
    uClCount[id * 3u + 2u] = coldCand | (kept << 16u);
    binned = n; keptOut = kept; candOut = liveCand + coldCand - live;
    overflow = dropped != 0u && uBinCap <= 0;
}

void main()
{
    if (gl_LocalInvocationIndex < 3u) sTally[gl_LocalInvocationIndex] = 0u;
    barrier();
    uint id = gl_GlobalInvocationID.x;
    if (id < uint(uGrid.x * uGrid.y * uGrid.z))
    {
        uint binned, kept, cand; bool overflow;
        Build(id, binned, kept, cand, overflow);
        if (overflow) atomicAdd(uStats[0], 1u);
        if (uTally != 0) { atomicAdd(sTally[0], binned); atomicAdd(sTally[1], kept); atomicAdd(sTally[2], cand); }
    }
    barrier();
    if (uTally != 0 && gl_LocalInvocationIndex < 3u) atomicAdd(uStats[1u + gl_LocalInvocationIndex], sTally[gl_LocalInvocationIndex]);
}

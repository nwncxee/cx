#include "cx_pass.glsl"
// CX-188 step 4: the (static light, mover) pair tiles. A light that traded its dyn page for them publishes
// uLocalDynPage[slot] NEGATIVE, so every `pg > 0.5` below reads it as pageless -- which is the truth, the page
// is gone. CxPairVis is where this file gets those movers back for an UNBAKED receiver (a creature standing in
// another creature's shadow under a lamp), in the stages that define CX_PAIR_RECV; a baked receiver of the
// deferred frame takes the same tiles through the debt dispatch instead, once per pixel.
// The include is inside the guard too, so a program that is not one of those two carries neither the pair
// table nor the atlas sampler -- this library compiles into VERTEX stages as well (grass, weather, particles),
// and an SSBO plus a unit-28 sampler there would be resources spent on code that cannot run. A hak override of
// cx_pairshad therefore reaches exactly the stages that read it.
#ifdef CX_PAIR_RECV
#include "cx_pairshad.glsl"
#endif
uniform samplerCubeArrayShadow uLocalShadow;
// CX-43: per PROGRAM, not in the pass block -- the particle pass zeroes it under /particles shadows 0
// and the bake reads its one scratch cube. 0 = no cubes to sample (vanilla, no lights, not yet drawn).
uniform int uLocalCubes;
uniform float uLocalSoftWorld;   // #674 bake only: the disc is at least this wide in world units (one lightmap texel), else its cube-texel edge aliases against the page

// #305: the DYNAMIC pool -- movers only, re-baked every frame at reduced resolution, composed with
// the static cube by min(): depth visibility is a min-of-blockers, so the two arrays compose exactly
// and NO COPY exists anywhere on the path (#187's composite cache lost to its own per-view copy;
// this is not that design). uLocalDynPage[slot] carries the pool PAGE PLUS ONE (0 = no movers near
// that light).
uniform samplerCubeArrayShadow uLocalDynShadow;
// #642: the same two arrays through a sampler object with NO compare, so the blocker search reads a
// depth rather than a verdict. Bound by layout past the 0..15 table (TextureSlots.LocalShadowRaw);
// the driver's unit count is asked once and where it is short uLocalPcss.x stays 0, which stands the
// search down (a fixed disc at the ceiling) -- see CasterPass.RawSearch.
layout(binding = 16) uniform samplerCubeArray uLocalShadowRaw;
layout(binding = 17) uniform samplerCubeArray uLocalDynShadowRaw;

// Shadow-accuracy pass: every offset/bias below is HALF its #308 value (the stacked push reached 8
// texels = 0.5-1u of peter-panning on the outer cascades). Creature self-shadowing stays off
// (CX_LOCAL_DYN_NEAR), so the low-poly acne these were sized against no longer needs covering.
// Sample point pushed along the normal by this many cube texels -- the same acne fight the sun
// cascades run, scaled by the texel a face actually spans at this distance rather than a constant.
const float CX_LOCAL_NORMAL_OFFSET = 1.0;
// Depth bias in WORLD units along the face's major axis, applied before the projection so it stays a
// constant distance whatever the light's reach is.
const float CX_LOCAL_BIAS = 0.02;
// #308 follow-up (acne): the sun receiver's slope terms, same reasoning -- a face lit at grazing
// incidence needs offset and bias that grow with tan(theta), or the terminator stair-steps.
const float CX_LOCAL_SLOPE_OFFSET = 0.75;
const float CX_LOCAL_SLOPE_MAX = 2.0;
// #308 follow-up: fragments ON A CREATURE (skinmesh or dangly -- exactly the classes the caster
// walk puts in the dynamic set every frame) skip the dyn-pool tap when they stand within this many
// times the near-field skip (uLocalSoft.y) of the light. In that band the pool's blocker is the
// carrier's own body, whose pose-vs-bake mismatch is a flicker that cannot be made accurate at pool
// resolution -- so creature self-shadowing from a carried light is OFF, while the creature's shadow
// on the GROUND (a static fragment, never gated) and every static-map shadow stay untouched.
// Scales with /shadow near, so the cutoff is tunable live.
const float CX_LOCAL_DYN_NEAR = 3.0;
// #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
// 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
// actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
// along it walks the sample point sideways across the shadow map's texel grid and turns a clean
// lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
// what it needs.
// Unset reads 0, which is the surface behaviour every existing program and every content override
// already relies on -- the safe default is the one nobody has to declare.
uniform float uLocalMedia;
// #641 uLocalFloor: the fraction of a point light's DIFFUSE that still arrives inside its own shadow,
// standing in for the indirect light Alloy has none of; the light's own colour rides in the diffuse
// term it multiplies, so a torch shadow stays warm.
// #642 uLocalPcss: x = the light's source radius in world units (the penumbra scale; 0 = no blocker
// search, the disc sits at its ceiling), y = the temporal rotation phase 0..1 (0 when the taa stage is
// not live, so the dither is a fixed pattern rather than a shimmer), z = disc taps.
// Below this radius (in texels) the disc stands down to the hardware tap: a fragment in contact.
const float CX_LOCAL_DISC_MIN = 0.25;
const float CX_LOCAL_DISC_WORLD_MAX = 64.0;   // #674 bake: the world-minimum disc's ceiling in cube texels
// CX-49: the CEILING on ring taps in the blocker search -- shadow.search picks the count under it, and
// ladders on the quality tier. Four (the pre-CX-49 count, now the low rung) read one tap per quadrant,
// which on a wide ceiling missed whole casters and estimated the penumbra off whichever quadrant held one.
const int CX_LOCAL_SEARCH = 16;
// shadow.search, under that ceiling. A caller that fills the pass block itself (the lightmap bake) leaves
// the member zero, which reads here as the ceiling -- the bake is offline and wants the best estimate.
int CxSearchTaps() { int n = int(uSearchTaps); return n > 0 ? min(n, CX_LOCAL_SEARCH) : CX_LOCAL_SEARCH; }
// #642: the per-fragment dither the disc spins by. Interleaved gradient noise on the pixel where a
// fragment head declares CX_FS; a vertex stage (grass, weather, particles compile this lib there
// too) has no gl_FragCoord, and a head that forgets the define gets the coarser hash, not a
// compile failure.
// CX-188: the pixel comes through CX_PIXEL so a COMPUTE stage can supply its own -- cx_deferred_cs has a
// pixel but no gl_FragCoord, and must dither exactly as the fragment that wrote the record would have.
#if defined(CX_FS) && !defined(CX_PIXEL)
#define CX_PIXEL gl_FragCoord.xy
#endif
#ifdef CX_PIXEL
float CxShadowNoise(vec3 worldPos) { return fract(52.9829189 * fract(0.06711056 * CX_PIXEL.x + 0.00583715 * CX_PIXEL.y)); }
#else
float CxShadowNoise(vec3 worldPos) { return fract(sin(dot(floor(worldPos * 64.0), vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
#endif
// A Vogel disc of up to CX_VOGEL_MAX taps before its 1/sqrt(n) scale: tap i is
// sqrt(i + 0.5) * (cos, sin)(i * golden angle). The per-light trig lives in CxSpin, once; the per-tap
// direction turns by CX_GOLDEN (CxVogelTurn) rather than reading a table -- a const array indexed in a
// loop is what NVIDIA materialises in local memory, per fragment, on every lit fragment's entry.
const int CX_VOGEL_MAX = 16;
const vec2 CX_GOLDEN = vec2(-0.737368878, 0.675490294);   // (cos, sin) of the golden angle
vec2 CxVogelTurn(vec2 v) { return vec2(v.x * CX_GOLDEN.x - v.y * CX_GOLDEN.y, v.x * CX_GOLDEN.y + v.y * CX_GOLDEN.x); }
// The disc of n taps at `radius`, spun by `rot` turns: tap i is CxSpin(...) * sqrt(i + 0.5) * dir_i.
mat2 CxSpin(int n, float rot, float radius)
{
    float a = rot * 6.283185, s = radius * inversesqrt(float(n));
    return mat2(cos(a), sin(a), -sin(a), cos(a)) * s;
}
// The stored window depth back to a major-axis distance: the inverse of the `ref` line below.
float CxCubeDist(float z, float near, float far) { return near / max(1.0 - z * (far - near) / far, 1e-4); }
// #642: the penumbra radius in texels, 0 .. uLocalSoft.x -- PCSS: the mean depth of the blockers found
// by CX_LOCAL_SEARCH raw reads on a ring (plus `mc`, the centre read the caller already has), then
// (receiver - blocker) / blocker scaled by the source radius. The ring is
// NOT dithered: neighbouring fragments must agree on the penumbra, or the edge speckles where one
// fragment blurs and the next takes the hard tap (seen live). A search that finds no blocker is a
// hard tap; uLocalPcss.x = 0 (no raw view on this driver) is the ceiling everywhere. `full` = every
// read found a blocker: the disc would only re-confirm the shadow the hardware tap already holds.
// CX-49: the ring's RADIUS comes off the source size and the two depths rather than sitting at the
// ceiling. The centre read already has a blocker depth, and the widest penumbra a blocker at mc can cast
// on a receiver at m is src * (m - mc) / mc world units, so a ring wider than that samples texels whose
// casters cannot reach this fragment at all. A centre that found nothing keeps the ceiling, which is the
// only honest bound where no depth is in hand yet.
float CxPenumbra(samplerCubeArray raw, float layer, vec3 d, vec3 u1, vec3 v1,
                 float m, float mc, float near, float far, float texel, out bool full)
{
    full = false;
    float src = uLocalPcss.x;
    if (src <= 0.0) return uLocalSoft.x;
    float sum = 0.0, n = 0.0;
    if (mc < m) { sum += mc; n += 1.0; }
    float sr = mc < m ? min(uLocalSoft.x, src * (m - mc) / (mc * texel)) : uLocalSoft.x;
    int ns = CxSearchTaps();
    mat2 ring = CxSpin(ns, 0.0, texel * sr);
    vec2 dir = vec2(1.0, 0.0);
    for (int k = 0; k < ns; k++)
    {
        vec2 o = ring * (sqrt(float(k) + 0.5) * dir);
        dir = CxVogelTurn(dir);
        float mk = CxCubeDist(texture(raw, vec4(d + u1 * o.x + v1 * o.y, layer)).r, near, far);
        if (mk < m) { sum += mk; n += 1.0; }
    }
    if (n <= 0.0) return 0.0;
    full = n > float(ns);
    float mb = sum / n;
    return clamp((m - mb) / mb * src / texel, 0.0, uLocalSoft.x);
}

// Visibility of world point `worldPos` from the light in cube slot `slot`, 0 (shadowed) .. 1 (lit).
// The reference depth is the exact inverse of ShadowMath.LinearizeFaceDepth: for a 90deg face fitted
// to [near, far], the window depth stored at major-axis distance m is far/(far-near) * (1 - near/m).
// ShadowMath's SelfCheck welds that round-trip headless, so a convention slip fails there, not here.
// `ndlRaw` is the caller's geometric dot(N, L) -- already in hand at every lit call site -- for the
// slope terms; recomputing it here cost a normalize per fragment per shadowing light.
// `soft` false = the hardware tap alone, no blocker search and no disc (a mirror highlight's shadow).
float CxLocalShadowQ(int slot, vec3 worldPos, vec3 N, float ndlRaw, float onMover, float viewDepth, bool soft)
{
    if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
    vec4 L = uLocalLight[slot];
    float far = L.w, near = uLocalPlane.x;
    vec3 d = worldPos - L.xyz;
    float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
    // outside the recorded shell there is no depth to compare against, and lit is the safe answer
    if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
    // #139: inside the source sphere there is nothing to occlude. A carried torch sits INSIDE the
    // hand and forearm holding it, so a punctual test there shadows the carrier from its own light
    // -- and at that range one cube texel spans most of the arm, which is the blocky self-shadow.
    // CARRIER ONLY: on the ground this box (Chebyshev, world-aligned) drew as a lit diamond around
    // every fire placeable -- the sun's shadow apparently cut out in a square.
    if (onMover > 0.5 && m < uLocalSoft.y) return 1.0;
    float texel = 2.0 * m * uLocalPlane.y;
    // The acne offset/bias must clear the COARSEST map that will answer this fragment: a light with
    // an occupied dynamic page samples that page too, at (typically) half the static resolution.
    // Queried off the array itself so a stale content override merely lacks the fix. The PCF disc
    // below keeps the STATIC texel -- it only filters the static map, and widening it whenever a
    // mover claims the page would pop the penumbra on an unrelated event.
    float pg = uLocalDynPage[slot];
    float biasTexel = 2.0 * m * max(uLocalPlane.y,
                                    pg > 0.5 ? 1.0 / float(textureSize(uLocalDynShadow, 0).x) : 0.0);
    // tan of the light-to-normal angle, media-gated exactly as the sun receiver's slope is and for
    // the same billboard reason -- and zero on a light-backfacing fragment, where there is no acne
    // to fight and a pinned-at-max slope would only punch the bias through thin casters.
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1),
                        CX_LOCAL_SLOPE_MAX) * (1.0 - uLocalMedia);
    d += N * (biasTexel * (CX_LOCAL_NORMAL_OFFSET * (1.0 - uLocalMedia)
                           + CX_LOCAL_SLOPE_OFFSET * slope));
    // the bias moves the compare point TOWARD the light, never away: the test is `ref <= stored` =
    // lit, and a fragment lying on its own recorded surface stores exactly its own depth. Pushing
    // the reference the other way makes every lit surface shadow itself.
    m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
    float ref = far / (far - near) * (1.0 - near / m);
    float vs = texture(uLocalShadow, vec4(d, float(slot)), ref);
    // #308 follow-up: the dyn tap stands down for a CREATURE fragment inside the carrier band --
    // see CX_LOCAL_DYN_NEAR. Ground and static receivers pass onMover 0 and are never gated.
    bool dyn = pg > 0.5 && !(onMover > 0.5 && m < CX_LOCAL_DYN_NEAR * uLocalSoft.y);
    float vd = dyn ? texture(uLocalDynShadow, vec4(d, pg - 1.0), ref) : 1.0;
    // CX-188 step 4: a PAIRED light has no page (pg < 0) and its movers are in the pair tiles instead. Same
    // carrier band as the page tap above -- a creature inside its own carried light does not self-shadow --
    // and the raw worldPos, since CxPairTapAt applies the tile's own offsets rather than the cube's.
    // ONLY where CX_PAIR_RECV says so, which is the deferred stage and nothing else. That is where an unbaked
    // receiver is lit -- a creature is opaque and the twin draws it -- and keeping the tap out of every other
    // program is the whole lesson of the reverted pair-shadow attempt (504b6e0): its branches were compiled
    // into the forward light loop three times and the baseline with the feature OFF grew by 7.8 ms.
#ifdef CX_PAIR_RECV
    if (pg < -0.5 && !(onMover > 0.5 && m < CX_LOCAL_DYN_NEAR * uLocalSoft.y))
        vd = CxPairVis(slot, worldPos, N, ndlRaw, soft);
#endif
    float v = min(vs, vd);
    // #326: one tap when the disc is under its pixel budget on screen (z = px / lod scale) -- four of
    // every five cube fetches
    if (!soft || uLocalSoft.x <= 0.0 || (uLocalSoft.z > 0.0 && texel * uLocalSoft.x < viewDepth * uLocalSoft.z)) return mix(1.0, v, uLocalFade[slot]);
    // #642: the centre's raw depth, the blocker search's first sample
    float mc = uLocalPcss.x > 0.0 ? CxCubeDist(texture(uLocalShadowRaw, vec4(d, float(slot))).r, near, far) : m;
    // #642: contact-hardening. The disc radius is not uLocalSoft.x but a per-fragment penumbra
    // estimate with uLocalSoft.x as its ceiling -- see CxPenumbra.
    vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 u1 = normalize(cross(axis, d));
    vec3 v1 = normalize(cross(d, u1));
    float rot = fract(CxShadowNoise(worldPos) + uLocalPcss.y);
    // Full occlusion on either map is min()'s answer already: the hardware tap stands. Not in the bake,
    // whose search still reads the receiver's own plane as a blocker next to a floor-level light.
    bool full;
    float rs = max(CxPenumbra(uLocalShadowRaw, float(slot), d, u1, v1, m, mc, near, far, texel, full),
                   min(uLocalSoftWorld / texel, CX_LOCAL_DISC_WORLD_MAX));
    if (full && uLocalSoftWorld <= 0.0) return mix(1.0, v, uLocalFade[slot]);
    float rd = 0.0, dtex = 0.0;
    if (dyn)
    {
        dtex = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
        float md = uLocalPcss.x > 0.0 ? CxCubeDist(texture(uLocalDynShadowRaw, vec4(d, pg - 1.0)).r, near, far) : m;
        rd = CxPenumbra(uLocalDynShadowRaw, pg - 1.0, d, u1, v1, m, md, near, far, dtex, full);
        if (full && uLocalSoftWorld <= 0.0) return mix(1.0, v, uLocalFade[slot]);
    }
    // #642: a rotated Vogel disc at the estimated radius. The cube fetch is already hardware PCF, so
    // this WIDENS that filter rather than replacing it. The reference depth is deliberately the
    // centre tap's for every tap -- re-deriving it per tap would compare each neighbour against its
    // own surface and dissolve the penumbra it is there to create.
    // ponytail: one disc for both maps at its own radius each; no per-tap weighting, no bilateral term.
    int taps = min(int(uLocalPcss.z), CX_VOGEL_MAX);
    if (rs >= CX_LOCAL_DISC_MIN && taps > 0)
    {
        mat2 disc = CxSpin(taps, rot, texel * rs);
        vec2 dir = vec2(1.0, 0.0);
        for (int i = 0; i < taps; i++)
        {
            vec2 o = disc * (sqrt(float(i) + 0.5) * dir);
            dir = CxVogelTurn(dir);
            vec3 dt = d + u1 * o.x + v1 * o.y;
            float rt = ref;
            if (uLocalSoftWorld > 0.0)
            {   // bake: next to a floor-level light this disc spans many cube texels, and against the
                // centre's depth the receiver's own plane reads as a blocker (a dark disc round every
                // brazier) -- each tap compares against the plane's depth along its own ray instead
                float dn = dot(dt, N);
                float s = abs(dn) > 1e-4 ? clamp(dot(d, N) / dn, 0.5, 2.0) : 1.0;
                float mt = clamp(max(max(abs(dt.x), abs(dt.y)), abs(dt.z)) * s - CX_LOCAL_BIAS * (1.0 + slope), near, far);
                rt = far / (far - near) * (1.0 - near / mt);
            }
            vs += texture(uLocalShadow, vec4(dt, float(slot)), rt);
        }
        vs /= float(taps + 1);
    }
    if (dyn && rd >= CX_LOCAL_DISC_MIN && taps > 0)
    {
        mat2 disc = CxSpin(taps, rot, dtex * rd);
        vec2 dir = vec2(1.0, 0.0);
        for (int i = 0; i < taps; i++)
        {
            vec2 o = disc * (sqrt(float(i) + 0.5) * dir);
            dir = CxVogelTurn(dir);
            vd += texture(uLocalDynShadow, vec4(d + u1 * o.x + v1 * o.y, pg - 1.0), ref);
        }
        vd /= float(taps + 1);
    }
    return mix(1.0, min(vs, vd), uLocalFade[slot]);
}
float CxLocalShadow(int slot, vec3 worldPos, vec3 N, float ndlRaw, float onMover, float viewDepth)
{
    return CxLocalShadowQ(slot, worldPos, N, ndlRaw, onMover, viewDepth, true);
}

// #674: the static cube's visibility exactly as the lightmap bake filtered it (LightmapBake.BakeLight):
// PCSS off, so a fixed disc at the softness ceiling, never under `lmTexel` (the receiver's lightmap texel
// in world units), each tap against the receiver plane's depth along its own ray. The live filter is
// harder near contact, and a baked penumbra it read as umbra cut a creature's shadow off there. Unfaded.
// `soft` false = the centre tap alone (a hot light past the fragment's cap).
float CxLocalBakedVis(int slot, vec3 worldPos, vec3 N, float ndlRaw, float lmTexel, bool soft)
{
    vec4 L = uLocalLight[slot];
    float far = L.w, near = uLocalPlane.x;
    vec3 d = worldPos - L.xyz;
    float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
    if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
    float texel = 2.0 * m * uLocalPlane.y;
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_LOCAL_SLOPE_MAX);
    d += N * (texel * (CX_LOCAL_NORMAL_OFFSET + CX_LOCAL_SLOPE_OFFSET * slope));
    m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
    float ref = far / (far - near) * (1.0 - near / m);
    float vs = texture(uLocalShadow, vec4(d, float(slot)), ref);
    int taps = min(int(uLocalPcss.z), CX_VOGEL_MAX);
    float rs = max(uLocalSoft.x, min(lmTexel / texel, CX_LOCAL_DISC_WORLD_MAX));
    if (!soft || uLocalSoft.x <= 0.0 || taps <= 0 || rs < CX_LOCAL_DISC_MIN) return vs;
    vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 u1 = normalize(cross(axis, d));
    vec3 v1 = normalize(cross(d, u1));
    mat2 disc = CxSpin(taps, fract(CxShadowNoise(worldPos) + uLocalPcss.y), texel * rs);
    vec2 dir = vec2(1.0, 0.0);
    for (int i = 0; i < taps; i++)
    {
        vec2 o = disc * (sqrt(float(i) + 0.5) * dir);
        dir = CxVogelTurn(dir);
        vec3 dt = d + u1 * o.x + v1 * o.y;
        float dn = dot(dt, N);
        float sc = abs(dn) > 1e-4 ? clamp(dot(d, N) / dn, 0.5, 2.0) : 1.0;
        float mt = clamp(max(max(abs(dt.x), abs(dt.y)), abs(dt.z)) * sc - CX_LOCAL_BIAS * (1.0 + slope), near, far);
        vs += texture(uLocalShadow, vec4(dt, float(slot)), far / (far - near) * (1.0 - near / mt));
    }
    return vs / float(taps + 1);
}

// CX-56: the MOVERS' visibility alone from the light in `slot` -- its dyn page, no static map, unfaded;
// 1.0 where the light holds no page. `soft` = a 4-tap disc one dyn texel wide (a baked receiver has no
// static cube to min() it against, so the lone hardware tap shows its texel); false = the centre tap.
float CxMoverVisQ(int slot, vec3 worldPos, vec3 N, float ndlRaw, bool soft)
{
    if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
    float pg = uLocalDynPage[slot];
    if (pg < 0.5) return 1.0;
    vec4 L = uLocalLight[slot];
    float far = L.w, near = uLocalPlane.x;
    vec3 d = worldPos - L.xyz;
    float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
    if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
    float t = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_LOCAL_SLOPE_MAX);
    d += N * (t * (CX_LOCAL_NORMAL_OFFSET + CX_LOCAL_SLOPE_OFFSET * slope));
    m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
    float ref = far / (far - near) * (1.0 - near / m);
    if (!soft) return texture(uLocalDynShadow, vec4(d, pg - 1.0), ref);
    vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 u1 = normalize(cross(axis, d)) * t, v1 = normalize(cross(d, u1)) * t;
    return 0.25 * (texture(uLocalDynShadow, vec4(d + u1 + v1, pg - 1.0), ref) + texture(uLocalDynShadow, vec4(d - u1 + v1, pg - 1.0), ref)
                 + texture(uLocalDynShadow, vec4(d + u1 - v1, pg - 1.0), ref) + texture(uLocalDynShadow, vec4(d - u1 - v1, pg - 1.0), ref));
}

// #674: the visibility a baked receiver still owes a hot static light with NO mover channel (CX-56: past
// the four a surface can hold): the movers' occlusion scaled by an ESTIMATE of what the bake left lit --
// the static cube through the bake's own filter. The channel path reads the bake's answer instead.
float CxLocalDynShadowQ(int slot, vec3 worldPos, vec3 N, float ndlRaw, bool soft, float lmTexel)
{
    float vd = CxMoverVisQ(slot, worldPos, N, ndlRaw, soft);
    if (vd >= 1.0) return 1.0;
    return mix(1.0, 1.0 - CxLocalBakedVis(slot, worldPos, N, ndlRaw, lmTexel, soft) * (1.0 - vd), uLocalFade[slot]);
}

/// CX-188 step 3 (ADR 0029): the DEFERRED path's filter -- what the per-pixel visibility record is filled
/// with (cx_defshadow_cs). Same verdict as <see cref="CxLocalShadowQ"/>, a quarter of the fetches:
/// <list>
/// <item>the static cube's disc is CX_LOCAL_FAST_TAPS, not uLocalPcss.z, and each tap is already a hardware
/// 2x2 PCF, so four of them is a 4x4 kernel's worth of compares;</item>
/// <item>the dyn page gets the centre tap alone -- its own disc was eight more fetches on a 256px map whose
/// texel the static disc's min() already softens;</item>
/// <item>the blocker search runs only where the caller asks (`search`), which the record pass does for the
/// pixel's STRONGEST light and no other: contact hardening is what makes a figure's feet meet the ground,
/// and the weaker lights' penumbrae sit at the softness ceiling, which is the safe error on a dim term.</item>
/// </list>
/// `rot` is the disc's rotation in turns, supplied rather than dithered: the record pass interleaves four
/// fixed rotations across a 2x2 block of record texels, so the pattern is deterministic and reads as a soft
/// edge with TAA off (no dither for a temporal pass to resolve).
/// <para>ponytail: the reference derivation above the fetches is a second copy of CxLocalShadowQ's head. It is
/// not shared because the forward filter must keep compiling to exactly what it does today and three CX-188
/// streams are editing this file at once; fold the two into one helper once they have merged.</para>
const int CX_LOCAL_FAST_TAPS = 4;
float CxLocalShadowFast(int slot, vec3 worldPos, vec3 N, float ndlRaw, float onMover, float viewDepth,
                        float rot, bool search)
{
    if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
    vec4 L = uLocalLight[slot];
    float far = L.w, near = uLocalPlane.x;
    vec3 d = worldPos - L.xyz;
    float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
    if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
    if (onMover > 0.5 && m < uLocalSoft.y) return 1.0;
    float texel = 2.0 * m * uLocalPlane.y;
    float pg = uLocalDynPage[slot];
    float biasTexel = 2.0 * m * max(uLocalPlane.y,
                                    pg > 0.5 ? 1.0 / float(textureSize(uLocalDynShadow, 0).x) : 0.0);
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1),
                        CX_LOCAL_SLOPE_MAX) * (1.0 - uLocalMedia);
    d += N * (biasTexel * (CX_LOCAL_NORMAL_OFFSET * (1.0 - uLocalMedia)
                           + CX_LOCAL_SLOPE_OFFSET * slope));
    m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
    float ref = far / (far - near) * (1.0 - near / m);
    float vs = texture(uLocalShadow, vec4(d, float(slot)), ref);
    bool dyn = pg > 0.5 && !(onMover > 0.5 && m < CX_LOCAL_DYN_NEAR * uLocalSoft.y);
    float vd = dyn ? texture(uLocalDynShadow, vec4(d, pg - 1.0), ref) : 1.0;
    // CX-188 step 4: a PAIRED light has no page and its movers are in the pair tiles instead -- and on the
    // deferred path THIS filter, not CxLocalShadowQ, is what writes an unbaked receiver's shadow verdict
    // (cx_defshadow_cs), so the tap has to be here as well or a creature takes no shadow from another
    // creature under a lamp. One tile tap, the lone hardware one, matching this filter's own cheapness.
#ifdef CX_PAIR_RECV
    if (pg < -0.5 && !(onMover > 0.5 && m < CX_LOCAL_DYN_NEAR * uLocalSoft.y))
        vd = CxPairVis(slot, worldPos, N, ndlRaw, false);
#endif
    float v = min(vs, vd);
    int taps = min(int(uLocalPcss.z), CX_LOCAL_FAST_TAPS);
    // #326's screen-radius gate and the softness knobs, read exactly as the live filter reads them
    if (uLocalSoft.x <= 0.0 || taps <= 0
        || (uLocalSoft.z > 0.0 && texel * uLocalSoft.x < viewDepth * uLocalSoft.z))
        return mix(1.0, v, uLocalFade[slot]);
    vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 u1 = normalize(cross(axis, d));
    vec3 v1 = normalize(cross(d, u1));
    float rs = uLocalSoft.x;
    if (search && uLocalPcss.x > 0.0)
    {
        bool full;
        float mc = CxCubeDist(texture(uLocalShadowRaw, vec4(d, float(slot))).r, near, far);
        rs = CxPenumbra(uLocalShadowRaw, float(slot), d, u1, v1, m, mc, near, far, texel, full);
        // every search read found a blocker: min()'s answer is the hardware tap's already
        if (full) return mix(1.0, v, uLocalFade[slot]);
    }
    if (rs < CX_LOCAL_DISC_MIN) return mix(1.0, v, uLocalFade[slot]);
    mat2 disc = CxSpin(taps, rot, texel * rs);
    vec2 dir = vec2(1.0, 0.0);
    for (int i = 0; i < taps; i++)
    {
        vec2 o = disc * (sqrt(float(i) + 0.5) * dir);
        dir = CxVogelTurn(dir);
        // the centre tap's reference for every tap, as the live disc does: re-deriving it per tap compares
        // each neighbour against its own surface and dissolves the penumbra
        vs += texture(uLocalShadow, vec4(d + u1 * o.x + v1 * o.y, float(slot)), ref);
    }
    return mix(1.0, min(vs / float(taps + 1), vd), uLocalFade[slot]);
}

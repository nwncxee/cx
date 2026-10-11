#version 460 core
// CX-51: screen-space reflections, one half-res pass after the opaque set, sky, cutouts and grass (Ssr). Each
// texel traces for one pixel of its 2x2 block -- the one whose reflection record (cx_reflrec) weighs most -- through
// THIS frame's depth, as a hi-Z pyramid:
// the walk climbs while the ray stays in front of each cell's closest surface and confirms a hit at the finest
// level. Out goes the correction the lit stage's probe term needs, (screen - probe) x trust, and the traced pixel's
// 1 / view depth; cx_ssrcomp_f joins it to the full-res pixels at that depth and scales it by each one's own weight. On flat ground at the player's height the mirrored draw (uPlanar) answers
// first. A miss, a back face or a ray off the screen leaves the probe standing -- never the screen at the ray's
// vanishing point: that swaps sky for probe along a line that slides with the camera's pitch.
#include "cx_ssr.glsl"
#include "cx_ssrrec.glsl"
in vec2 vUV;
layout(location = 0) out vec4 frag;
uniform sampler2D uDepth;    // the frame depth
uniform sampler2D uColor;    // the frame so far, linear HDR, mipmapped, its level 0 at level uColShift of the frame
uniform int uColShift;
uniform sampler2D uHiZ;      // 1 / view depth: level 0 per pixel, each level up the MAX (closest) of the one below
uniform sampler2D uRecN;     // the record: octahedral world normal, roughness, view depth (cx_ssrrec)
uniform sampler2D uRecW;     // ...its weight
uniform samplerCube uProbe;  // the probe the lit stage added, to be taken back out where the screen answers
uniform sampler2D uPlanar;   // the opaque set drawn mirrored across uPlane.x, half res, mipped: alpha 1 where it drew
uniform vec4 uPlane;         // x = the mirror plane's world z, y = 1 when uPlanar holds this frame's
uniform mat4 uProj, uInvProj, uView, uInvView;
uniform vec2 uNf;            // near, far
uniform vec4 uSsr;           // x = roughness cap, y = cell visits per ray, z = probe mips (0 = no probe), w = finest hi-Z level
uniform int uViz;            // 41 = trust / weight, 42 = the reflection, 45 = the probe a miss falls back to, 46 = the mirrored draw

/// The pyramid level the walk may climb to (a 64-pixel cell): coarser cells overlap too much of the scene to skip.
const int CX_SSR_TOP = 6;
/// The longest reflection, in view units.
const float CX_SSR_DIST = 60.0;
/// How far behind a pixel's surface the ray may sit and still hit it: a floor, and a share of the distance for
/// depth precision. Thin on purpose -- a thicker skin reads a ray passing BEHIND an object as a hit on it, which
/// draws a stretched second copy of the object below its reflection.
const float CX_SSR_THICK = 0.15, CX_SSR_THICKREL = 0.015;
/// The screen-edge fade, in uv: a hit this close to the border fades toward the probe instead of cutting.
const float CX_SSR_EDGE = 0.08;
/// How far (world z) a surface may sit off the mirror plane and still take the mirrored draw, faded over this range:
/// a curb's top is not the street's plane, and its mirror image would land a curb-height off.
const vec2 CX_PLANE_TOL = vec2(0.1, 0.4);
/// The world normal's z from which a surface counts as flat enough for the plane, faded in over this range.
const vec2 CX_PLANE_FLAT = vec2(0.9, 0.97);
/// How far (uv per unit of normal tilt) a rippled or bumpy surface shifts its read of the mirrored draw.
const float CX_PLANE_WARP = 0.04;
/// The share of the probe a screen hit may at most take away: the mirrored draw and the frame are lit without
/// the dynamic lights the probe saw, so a dark hit subtracted whole punched a black void into a lit street.
const float CX_SSR_FLOOR = 0.25;
/// The mirrored draw's mip at roughness 1 (on the sqrt(rough) ladder the probe uses).
/// ponytail: blur by roughness alone, not by the reflected point's height off the plane (no contact hardening).
const float CX_PLANE_LOD = 4.0;
vec3 ViewPos(vec2 uv, float depth)
{
    vec4 p = uInvProj * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    return p.xyz / p.w;
}

/// A surface the ray meets from behind or edge-on: what the mirror sees is past it, so the walk goes on. Only a
/// recorded surface knows its normal; an unrecorded one (grass, a hosted draw) is taken as facing.
bool Behind(ivec2 p, vec3 r)
{
    vec3 n, w; float rough;
    return CxRecRead(uRecN, uRecW, uHiZ, p, n, rough, w) && dot(mat3(uView) * n, r) >= 0.0;
}

/// What the screen shows along view-space ray r from view position o: rgb = radiance, a = trust.
vec4 Trace(vec3 o, vec3 r, float rough, vec2 size)
{
    float len = r.z > 0.0 ? min(CX_SSR_DIST, (-uNf.x * 1.01 - o.z) / r.z) : CX_SSR_DIST;
    int top = min(textureQueryLevels(uHiZ) - 1, CX_SSR_TOP);
    int fine = min(int(uSsr.w), top);
    if (len <= 0.0) return vec4(0.0);
    vec4 h0 = uProj * vec4(o, 1.0), h1 = uProj * vec4(o + r * len, 1.0);
    float k0 = 1.0 / h0.w, k1 = 1.0 / h1.w;
    vec2 p0 = (h0.xy * k0 * 0.5 + 0.5) * size, p1 = (h1.xy * k1 * 0.5 + 0.5) * size, d = p1 - p0;
    bvec2 moves = greaterThan(abs(d), vec2(1e-4));
    vec2 invD = 1.0 / mix(vec2(1.0), d, moves), dirS = step(0.0, d);
    vec2 tAx = mix(vec2(1.0), (mix(vec2(-1e-3), size + 1e-3, dirS) - p0) * invD, moves);
    float tEnd = clamp(min(tAx.x, tAx.y), 0.0, 1.0);
    float px = max(abs(d.x), abs(d.y));
    if (px * tEnd < 1.0) return vec4(0.0);
    // in t, a hundredth of a pixel: what nudges a step across the cell wall it stopped on
    float tEps = 0.01 / px;
    // (1/w is linear in screen space: the ray's inverse depth at t is mix(k0, k1, t))
    vec2 tx = mix(vec2(1e30), (floor(p0) + dirS - p0) * invD, moves);
    float t = min(tx.x, tx.y) + tEps;   // out of the pixel's own texel first
    int iter = int(uSsr.y), lv = max(fine, 1);
    bool found = false;
    for (int i = 0; i < iter && t < tEnd; i++)
    {
        float cs = float(1 << lv);
        vec2 cell = floor((p0 + d * t) / cs);
        tx = mix(vec2(1e30), ((cell + dirS) * cs - p0) * invD, moves);
        float tExit = min(min(tx.x, tx.y), tEnd);
        float kS = texelFetch(uHiZ, ivec2(cell), lv).r;
        float kA = mix(k0, k1, t), kB = mix(k0, k1, tExit);
        // in front of the closest surface across the whole cell: skip it, and try a coarser cell next
        if (min(kA, kB) > kS) { t = tExit + tEps; lv = min(lv + 1, top); continue; }
        // the ray reaches that surface inside the cell: move to where, if it is still ahead
        float tc = abs(k1 - k0) > 1e-9 ? (kS - k0) / (k1 - k0) : t;
        if (kA > kS) t = clamp(tc, t, tExit);
        if (lv > fine) { lv--; continue; }
        float ray = 1.0 / mix(k0, k1, t);
        if (ray - 1.0 / kS < max(CX_SSR_THICK, CX_SSR_THICKREL * ray) && !Behind(ivec2(p0 + d * t), r)) { found = true; break; }
        t = tExit + tEps;   // behind it, or its back: walk on at this level
    }
    if (!found) return vec4(0.0);
    vec2 ph = p0 + d * t, uv = ph / size, e = min(uv, 1.0 - uv);
    float w = smoothstep(0.0, CX_SSR_EDGE, min(e.x, e.y)) * (1.0 - smoothstep(0.8, 1.0, t));
    // the lobe's footprint widens with the distance travelled and picks the mip. rough^2, not alpha: a box mip of
    // a thin dark object blends in the bright sky around it, so a glossy mirror must stay near level 0
    vec3 hit = textureLod(uColor, uv, max(log2(max(distance(ph, p0) * rough * rough, 1.0)), float(fine)) - float(uColShift)).rgb;
    return vec4(hit, w);
}

void main()
{
    frag = vec4(0.0);
    ivec2 lim = textureSize(uHiZ, 0) - 1, px = ivec2(-1);
    vec3 n, w;
    float wmax = 0.0, rough = 1.0;
    for (int j = 0; j < 4; j++)
    {
        ivec2 q = min(ivec2(gl_FragCoord.xy) * 2 + ivec2(j & 1, j >> 1), lim);
        vec3 qn, qw; float qr;
        if (!CxRecRead(uRecN, uRecW, uHiZ, q, qn, qr, qw)) continue;
        float qm = max(qw.r, max(qw.g, qw.b));
        if (!(qm >= CX_SSR_WEIGHT.x) || qr >= uSsr.x || qm <= wmax) continue;
        px = q; n = qn; w = qw; wmax = qm; rough = qr;
    }
    if (px.x < 0) return;
    vec2 size = vec2(textureSize(uHiZ, 0));
    vec3 o = ViewPos((vec2(px) + 0.5) / size, texelFetch(uDepth, px, 0).r);
    vec3 r = reflect(normalize(o), normalize(mat3(uView) * n));
    vec3 P = (uInvView * vec4(o, 1.0)).xyz;
    // the mirrored draw where this is the plane: exact, no holes behind figures, undersides included
    vec2 uv = (vec2(px) + 0.5) / size + (mat3(uView) * vec3(n.xy, 0.0)).xy * CX_PLANE_WARP;
    vec4 pl = uPlane.y > 0.0 ? textureLod(uPlanar, uv, sqrt(rough) * CX_PLANE_LOD) : vec4(0.0);
    // min() hands back its other operand for a NaN on NVIDIA: the cap below would paint one CX_SSR_MAX white
    if (any(isnan(pl)) || any(isinf(pl))) pl = vec4(0.0);
    if (uViz == 46) { frag = vec4(pl.rgb, 1.0); return; }
    float pa = clamp(pl.a, 0.0, 1.0) * smoothstep(CX_PLANE_FLAT.x, CX_PLANE_FLAT.y, n.z)
             * (1.0 - smoothstep(CX_PLANE_TOL.x, CX_PLANE_TOL.y, abs(P.z - uPlane.x)));
    vec4 hit = pa < 1.0 ? Trace(o, r, rough, size) : vec4(0.0);
    if (any(isnan(hit)) || any(isinf(hit))) hit = vec4(0.0);
    hit = clamp(hit, vec4(0.0), vec4(vec3(CX_SSR_MAX), 1.0));
    pl.rgb = min(pl.rgb, vec3(CX_SSR_MAX));
    float gate = CxSsrGate(wmax, rough, uSsr.x);
    float trust = pa + (1.0 - pa) * hit.a;
    if (uViz == 41) { frag = vec4(trust * gate, wmax, 1.0, 1.0); return; }
    // the lit stage's own fetch, exactly: R in world space, its roughness on the prefilter's mip ladder
    vec3 env = uSsr.z > 0.0 ? CxProbeFetch(uProbe, P, mat3(uInvView) * r, sqrt(rough) * (uSsr.z - 1.0)) : vec3(0.0);
    if (uViz == 45) { frag = uSsr.z > 0.0 ? vec4(env, 1.0) : vec4(1.0, 0.0, 1.0, 1.0); return; }   // magenta = no probe bound
    // plane over screen over probe
    vec3 refl = max(mix(mix(env, hit.rgb, hit.a), pl.rgb, pa), env * CX_SSR_FLOOR);
    if (uViz == 42) { frag = vec4(refl * trust, 1.0); return; }
    frag = vec4(refl - env, texelFetch(uHiZ, px, 0).r);
    // a record some other program wrote garbage into can pass the depth test by chance: one NaN here is the
    // whole frame's, through the bloom
    if (any(isnan(frag)) || any(isinf(frag))) frag = vec4(0.0);
}

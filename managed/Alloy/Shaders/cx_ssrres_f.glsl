#version 460 core
// The reflection resolve, per full-res pixel: its own trace texel and three rotated neighbours', each hit weighed
// by THIS pixel's GGX lobe toward it over the pdf the ray was drawn with (Stachowiak 2015), so a block of four
// pixels shows four reflections. Each hit's colour comes off the frame's mip the lobe's cone covers at that
// distance, highlight-reduced (Karis) so one bright texel cannot outvote the rest. The mirrored draw (uPlanar)
// answers first on flat ground at the player's height, the probe where neither answers. Out goes the correction
// the lit stage's probe term needs, (reflection - probe) x this pixel's weight and gate; a = the hit's distance,
// which cx_ssrhist_f reprojects by.
#include "cx_ssr.glsl"
#include "cx_ssrrec.glsl"
layout(location = 0) out vec4 frag;
uniform sampler2D uTrace;    // cx_ssrtrace_f: xy = hit uv, z = pdf (0 = none), w = the origin's 1 / view depth
uniform sampler2D uDepth;    // the frame depth
uniform sampler2D uColor;    // the frame so far, linear HDR, mipmapped, its level 0 at level uColShift of the frame
uniform int uColShift;
uniform sampler2D uHiZ;      // level 0: 1 / view depth per full-res pixel
uniform sampler2D uRecN;
uniform sampler2D uRecW;
uniform samplerCube uProbe;  // the probe the lit stage added, to be taken back out where the screen answers
uniform sampler2D uPlanar;   // the opaque set drawn mirrored across uPlane.x, mipped: alpha 1 where it drew
uniform vec4 uPlane;         // x = the mirror plane's world z, y = 1 when uPlanar holds this frame's
uniform mat4 uInvProj, uView, uInvView;
uniform float uCap;          // the roughness cap
uniform float uProbeMips;    // the probe's mips (0 = no probe)
uniform int uShift;          // log2 of the trace's block
uniform int uFrame;          // the noise phase

/// How far (a share of view depth) a ray's origin may sit off this pixel and its hit still stand for it, faded
/// over the range: a neighbour on another surface saw another reflection.
const vec2 CX_SSR_JOIN = vec2(0.01, 0.03);
/// The neighbours whose hits a pixel reuses beside its own texel's, turned a quarter per drawn step.
const ivec2 CX_SSR_REUSE[3] = ivec2[3](ivec2(1, 0), ivec2(-1, 1), ivec2(0, -1));
/// The screen-edge fade, in uv: a hit this close to the border fades toward the probe instead of cutting.
const float CX_SSR_EDGE = 0.08;
/// How far (world z) a surface may sit off the mirror plane and still take the mirrored draw, faded over this range:
/// a curb's top is not the street's plane, and its mirror image would land a curb-height off.
const vec2 CX_PLANE_TOL = vec2(0.1, 0.4);
/// The world normal's z from which a surface counts as flat enough for the plane, faded in over this range.
const vec2 CX_PLANE_FLAT = vec2(0.9, 0.97);
/// How far (uv per unit of normal tilt) a rippled or bumpy surface shifts its read of the mirrored draw.
const float CX_PLANE_WARP = 0.04;
/// The share of the probe a screen hit keeps at least: the frame and the mirrored draw are lit without the
/// moving lights the probe saw, so a dark hit stands for a darker reflection, never for none.
const float CX_SSR_FLOOR = 0.25;
/// The mirrored draw's mip at roughness 1 (on the sqrt(rough) ladder the probe uses).
/// ponytail: blur by roughness alone, not by the reflected point's height off the plane (no contact hardening).
const float CX_PLANE_LOD = 4.0;

float Luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

void main()
{
    frag = vec4(0.0);
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec3 n, w; float rough;
    if (!CxRecRead(uRecN, uRecW, uHiZ, px, n, rough, w)) return;
    float gate = CxSsrGate(max(w.r, max(w.g, w.b)), rough, uCap);
    if (gate <= 0.0) return;
    vec2 size = vec2(textureSize(uHiZ, 0)), uv0 = (vec2(px) + 0.5) / size;
    float k = texelFetch(uHiZ, px, 0).r;
    vec3 o = CxViewPos(uInvProj, uv0, texelFetch(uDepth, px, 0).r);
    vec3 N = normalize(mat3(uView) * n), V = -normalize(o);
    float NoV = max(dot(N, V), 1e-4), a = max(rough, 1e-3), perceptual = sqrt(a);
    // the lobe's cone (Stride's fit, on perceptual roughness): its tangent, times the hit's screen distance, is the
    // footprint a mip covers
    float sp = sqrt(perceptual);
    float cone = perceptual * (1.0 - CX_SSR_BIAS) * NoV * sp * mix(clamp(NoV * 2.0, 0.0, 1.0), 1.0, sp);
    float maxMip = float(textureQueryLevels(uColor) - 1);
    ivec2 base = px >> uShift, lim = textureSize(uTrace, 0) - 1;
    int turn = int(CxSsrNoise(px, uFrame).x * 4.0);
    vec3 sum = vec3(0.0);
    float wsum = 0.0, seen = 0.0, hits = 0.0, dist = 0.0;
    for (int j = 0; j < 4; j++)
    {
        ivec2 off = j == 0 ? ivec2(0) : CX_SSR_REUSE[j - 1];
        for (int q = 0; q < turn; q++) off = ivec2(-off.y, off.x);
        vec4 t = texelFetch(uTrace, clamp(base + off, ivec2(0), lim), 0);
        float join = 1.0 - smoothstep(CX_SSR_JOIN.x, CX_SSR_JOIN.y, abs(t.w - k) / k);
        if (!(join > 0.0)) continue;
        seen += join;
        if (!(t.z > 0.0)) continue;
        vec3 h = CxViewPos(uInvProj, t.xy, texelFetch(uDepth, clamp(ivec2(t.xy * size), ivec2(0), ivec2(size) - 1), 0).r);
        vec3 L = h - o;
        float len = length(L);
        L /= max(len, 1e-4);
        float NoL = dot(N, L);
        if (NoL <= 0.0) continue;
        // the lobe toward the hit over the pdf it was drawn with: f x N.L is D x G x F / (4 N.V), and with G, F and
        // the per-pixel 4 N.V out (the sum is normalised) D is what is left.
        // ponytail: no Smith G -- it joins when a grazing lobe shows hits through its own masking
        float wt = join * CxGgxD(max(dot(N, normalize(V + L)), 0.0), a) / t.z;
        if (!(wt > 0.0) || isinf(wt)) continue;
        float lod = log2(max(cone * distance(t.xy, uv0) * max(size.x, size.y), 1.0)) - float(uColShift);
        vec3 c = min(textureLod(uColor, t.xy, clamp(lod, 0.0, maxMip)).rgb, vec3(CX_SSR_MAX));
        if (any(isnan(c))) continue;
        vec2 e = min(t.xy, 1.0 - t.xy);
        float edge = smoothstep(0.0, CX_SSR_EDGE, min(e.x, e.y));
        sum += c / (1.0 + Luma(c)) * wt;
        wsum += wt;
        hits += join * edge;
        dist += len * wt;
    }
    vec3 hit = vec3(0.0);
    float trust = 0.0, hitDist = 0.0;
    if (wsum > 0.0)
    {
        vec3 m = sum / wsum;
        hit = m / max(1.0 - Luma(m), 1e-3);
        trust = clamp(hits / max(seen, 1e-4), 0.0, 1.0);
        hitDist = dist / wsum;
    }
    vec3 P = (uInvView * vec4(o, 1.0)).xyz;
    // the mirrored draw where this is the plane: exact, no holes behind figures, undersides included
    vec2 puv = uv0 + (mat3(uView) * vec3(n.xy, 0.0)).xy * CX_PLANE_WARP;
    vec4 pl = uPlane.y > 0.0 ? textureLod(uPlanar, puv, perceptual * CX_PLANE_LOD) : vec4(0.0);
    // min() hands back its other operand for a NaN on NVIDIA: the cap below would paint one CX_SSR_MAX white
    if (any(isnan(pl)) || any(isinf(pl))) pl = vec4(0.0);
    pl.rgb = min(pl.rgb, vec3(CX_SSR_MAX));
    float pa = clamp(pl.a, 0.0, 1.0) * smoothstep(CX_PLANE_FLAT.x, CX_PLANE_FLAT.y, n.z)
             * (1.0 - smoothstep(CX_PLANE_TOL.x, CX_PLANE_TOL.y, abs(P.z - uPlane.x)));
    // the lit stage's own fetch, exactly: R in world space, its roughness on the prefilter's mip ladder
    vec3 R = mat3(uInvView) * reflect(-V, N);
    vec3 env = uProbeMips > 0.0 ? CxProbeFetch(uProbe, P, R, perceptual * (uProbeMips - 1.0)) : vec3(0.0);
    // plane over screen over probe
    vec3 refl = max(mix(mix(env, hit, trust), pl.rgb, pa), env * CX_SSR_FLOOR);
    // the hit's distance only where the screen's hit is most of what shows: elsewhere the surface itself reprojects
    frag = vec4((refl - env) * (gate * clamp(w, 0.0, 1.0)), trust * (1.0 - pa) > 0.5 ? hitDist : 0.0);
    if (any(isnan(frag)) || any(isinf(frag))) frag = vec4(0.0);
}

#version 460 core
// CX-24: the grass scatter. One workgroup per visible patch; each lane walks the patch's blades, drops
// the ones whose hashed rank is past the distance ladder's keep (before touching the face, so a far patch's
// thinned-out majority costs a hash each), regenerates the survivors from their face's seed -- nothing is
// baked -- culls them, and appends each to its draw: procedural blades near (command 0) or far (1), or its
// material's alpha-tested cards (2 + slot). The record is the card stage's instance layout (iPos, iParam) plus
// the ground's normal and the blade's fade (iNrm). A patch too big for one workgroup is split across Y.
layout(local_size_x = 64) in;
layout(std430, binding = 8) readonly buffer CxGrassFaces { vec4 uFace[]; };      // 3 per face
layout(std430, binding = 9) readonly buffer CxGrassPatches { uvec4 uPatch[]; };  // 2 per visible patch
#include "cx_gbladebuf.glsl"
layout(std430, binding = 10) writeonly buffer CxGrassOut { vec4 uOut[]; };
layout(std430, binding = 11) buffer CxGrassCmd { uint uCmd[]; };                  // DrawArraysIndirectCommand each
const uint CX_GRASS_RECV = 3u;  // GrassField.InstFloats / 4
uniform vec4 uPlanes[6];
uniform vec3 uEyePos;
uniform vec4 uLod;             // x full-density radius, y keep floor
uniform vec2 uFade;            // (fade start, cut-off) in world units
uniform float uSnowThin;       // share of blades gone under snow, by rank
uniform int uClamp;            // > 0: the pass that clamps each command's count to its region, over this many materials
uniform float uNear;           // blades nearer than this take the full-detail strip
uniform vec2 uClump;           // x clump cell size (world units), y clump strength 0..1
uniform float uCardKeep;       // share of CARD-cell blades kept: the blade density is the card density times its inverse
uniform ivec2 uBladeCap;       // records the near and far blade regions hold
uniform sampler2D uPushTex;
uniform vec3 uPushWindow;

uint CxHash(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x; }
float CxRnd(inout uint st) { st = CxHash(st + 0x9e3779b9u); return float(st >> 8) * (1.0 / 16777216.0); }
// GrassField.Keep, character for character
float CxGrassKeep(float d) { if (d <= uLod.x) return 1.0; float r = uLod.x / d; return max(uLod.y, r * r); }

void main()
{
    if (uClamp > 0)
    {
        if (gl_LocalInvocationIndex != 0u) return;
        uCmd[1] = min(uCmd[1], uint(uBladeCap.x));
        uCmd[5] = min(uCmd[5], uint(uBladeCap.y));
        for (int m = 0; m < uClamp; m++)
        {
            vec4 s = uGrassMat[m * CX_GRASS_MATV + 1];
            if (s.y >= 0.0) { uint c = (2u + uint(s.y)) * 4u + 1u; uCmd[c] = min(uCmd[c], uint(s.z)); }
        }
        return;
    }
    uvec4 p = uPatch[gl_WorkGroupID.x * 2u], q = uPatch[gl_WorkGroupID.x * 2u + 1u];
    uint first = p.x, faces = p.y, total = p.z, mat = q.x;
    if (faces == 0u) return;   // the face search below would never end: faces - 1 wraps
    float keepMax = uintBitsToFloat(p.w) * 1.2;   // keep at the patch's nearest point: no blade keeps more
    vec4 prob = CxGrassMat(mat, 0), info = CxGrassMat(mat, 1);
    float height = info.x;
    int cardSlot = int(info.y);
    uint cardCap = uint(info.z), bladeCells = uint(info.w);
    uint lanes = gl_WorkGroupSize.x * gl_NumWorkGroups.y;
    for (uint j = gl_WorkGroupID.y * gl_WorkGroupSize.x + gl_LocalInvocationID.x; j < total; j += lanes)
    {
        // the ladder's rank: uniform per blade and stable frame to frame, so the blades a patch keeps at any
        // distance are a spread sample of it, nested as the distance changes (no reshuffle under a moving eye)
        float rank = float(CxHash(j * 0x9e3779b1u ^ first * 0x85ebca6bu) >> 8) * (1.0 / 16777216.0);
        if (rank > keepMax) continue;
        if (uSnowThin > 0.0 && rank > 1.0 - uSnowThin) continue;
        uint lo = 0u, hi = faces - 1u;
        while (lo < hi) { uint mid = (lo + hi + 1u) >> 1u; if (floatBitsToUint(uFace[(first + mid) * 3u].w) <= j) lo = mid; else hi = mid - 1u; }
        uint f = (first + lo) * 3u;
        vec4 fa = uFace[f], fb = uFace[f + 1u], fc = uFace[f + 2u];
        uint st = CxHash(floatBitsToUint(fb.w) ^ ((j - floatBitsToUint(fa.w)) * 2654435761u));
        // the engine's own three-random normalise and scale jitter (FUN_0x4b3a00)
        float r0 = CxRnd(st), r1 = CxRnd(st), r2 = CxRnd(st), sum = r0 + r1 + r2;
        if (sum <= 0.0) { r0 = 1.0; sum = 1.0; }
        vec3 pos = (fa.xyz * r0 + fb.xyz * r1 + fc.xyz * r2) / sum;
        float rot = CxRnd(st) * 6.2831853;
        float scale = height * ((CxRnd(st) - 0.5) * 0.4 + 1.0);
        // cell 0 is the fallback, as in the engine: a degenerate table must not render another quadrant
        float pick = CxRnd(st), acc = 0.0;
        uint cell = 0u;
        for (uint k = 0u; k < 4u; k++) { acc += prob[k]; if (pick < acc) { cell = k; break; } }
        float bend = 0.7 + CxRnd(st) * 0.6;

        float dist = distance(uEyePos, pos);
        if (dist > uFade.y) continue;
        float kv = CxGrassKeep(dist);
        float lod = clamp((kv * 1.2 - rank) / (kv * 0.2), 0.0, 1.0);
        if (lod <= 0.0) continue;
        bool blade = ((bladeCells >> cell) & 1u) != 0u;
        // a card cell keeps the art's own density: the blade multiplier is thinned back out of it
        if (!blade && (cardSlot < 0 || CxRnd(st) >= uCardKeep)) continue;
        // a sphere around the card, generous for the far ladder's widening and the sway
        vec3 c = pos + vec3(0.0, 0.0, scale * 0.5);
        float rad = scale * 2.0;
        bool vis = true;
        for (int k = 0; k < 6 && vis; k++) vis = dot(uPlanes[k].xyz, c) + uPlanes[k].w >= -rad * length(uPlanes[k].xyz);
        if (!vis) continue;
        vec2 puv = (pos.xy - uPushWindow.xy) * uPushWindow.z;
        if (puv.x > 0.0 && puv.y > 0.0 && puv.x < 1.0 && puv.y < 1.0
            && textureLod(uPushTex, puv, 0.0).a > mix(0.30, 0.95, fract(bend * 7.31))) continue;

        uint cmd, cap;
        if (blade)
        {
            // clumping: blades in one cell lean the same way and share a height, so the field reads as
            // tufts rather than as an even fur
            uint ch = CxHash(floatBitsToUint(floor(pos.x / uClump.x)) * 73856093u ^ floatBitsToUint(floor(pos.y / uClump.x)) * 19349663u);
            float crot = float(ch & 0xffffu) * (6.2831853 / 65536.0), cs = 0.7 + float(ch >> 16) * (0.6 / 65536.0);
            vec2 dv = mix(vec2(cos(rot), sin(rot)), vec2(cos(crot), sin(crot)), uClump.y * 0.8);
            rot = atan(dv.y, dv.x);
            scale *= mix(1.0, cs, uClump.y);
            cmd = dist < uNear ? 0u : 1u;
            cap = uint(cmd == 0u ? uBladeCap.x : uBladeCap.y);
        }
        else { cmd = 2u + uint(cardSlot); cap = cardCap; }
        uint idx = atomicAdd(uCmd[cmd * 4u + 1u], 1u);
        if (idx >= cap) continue;
        uint o = (uCmd[cmd * 4u + 3u] + idx) * CX_GRASS_RECV;
        vec3 n = cross(fb.xyz - fa.xyz, fc.xyz - fa.xyz);
        n = length(n) > 0.0 ? normalize(n) : vec3(0.0, 0.0, 1.0);
        if (n.z < 0.0) n = -n;
        uOut[o] = vec4(pos, rot);
        uOut[o + 1u] = vec4(scale, blade ? float(mat * 4u + cell) : float(cell), rank, bend);
        uOut[o + 2u] = vec4(n, lod * (1.0 - smoothstep(uFade.x, uFade.y, dist)));
    }
}

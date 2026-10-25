#version 460 core
layout(local_size_x = 64) in;   // GroupSize
layout(std430, binding = 8) readonly buffer CxCullSrc     { uint uSrc[]; };
layout(std430, binding = 9) readonly buffer CxCullSphere  { vec4 uSph[]; };
layout(std430, binding = 10) writeonly buffer CxCullCmd    { uint uCmd[]; };
layout(std430, binding = 11) writeonly buffer CxCullOutIdx { int  uOut[]; };
layout(std430, binding = 12) buffer CxCullCount            { uint uCnt[]; };
layout(std430, binding = 13) buffer CxCullKey              { float uKey[]; };
layout(std430, binding = 14) buffer CxCullRej              { uint uRejBuf[]; };
#include "cx_cullblk.glsl"
uniform sampler2D uPyramid;

// One pyramid tap, addressed through the LEVEL-0 grid rather than the level's own.
// This is the fix for #243's first live bug. `textureLod(uv, L)` maps uv across the width of level
// L, which is floor(w0 / 2^L) -- and for a non-power-of-two screen that is NOT w0 / 2^L. At level 8
// of a 1272-wide pyramid the level is 4 texels representing 1024 source columns, so uv 0.9 (source
// column 1145) samples the texel covering columns 768..1023: a completely different part of the
// screen, with a completely unrelated max depth. Large screen boxes take the high LODs, and a
// distant tile's bounding cube IS a large screen box -- which is why the artefact was distant
// tileset geometry vanishing while near geometry (whose box leaves the screen and exits above)
// stayed. Converting to a level-0 texel first and shifting keeps the correspondence exact.
float CxPyr(vec2 uv, int lod)
{
    ivec2 lsz = max(ivec2(uPyrSize) >> lod, ivec2(1));
    ivec2 t = clamp(ivec2(uv * uPyrSize) >> lod, ivec2(0), lsz - ivec2(1));
    return texelFetch(uPyramid, t, lod).r;
}

// #287: LOD selection. The sphere's screen radius is r * uLodScale / depth, and the clip w of its
// centre IS that depth under a perspective projection -- so no eye position and no view matrix reach
// this shader. A scale of zero means the pass is not perspective and the test cannot be trusted.
bool CxSubPixel(vec3 c, float r)
{
    if (uLodPx <= 0.0 || uLodScale <= 0.0) return false;
    float w = (uVP * vec4(c, 1.0)).w;
    return w > 0.0 && r * uLodScale < uLodPx * w;
}

bool CxOccluded(vec3 c, float r)
{
    vec2 lo = vec2(1e30), hi = vec2(-1e30);
    float near = 1e30;
    for (int i = 0; i < 8; i++)
    {
        vec3 o = vec3((i & 1) != 0 ? r : -r, (i & 2) != 0 ? r : -r, (i & 4) != 0 ? r : -r);
        vec4 cp = uPyrVP * vec4(c + o, 1.0);
        // Straddling the eye plane: the screen box is meaningless there, so do not cull.
        if (cp.w <= 0.0) return false;
        vec3 ndc = cp.xyz / cp.w;
        lo = min(lo, ndc.xy); hi = max(hi, ndc.xy); near = min(near, ndc.z);
    }
    // Partly off screen: CLAMP_TO_EDGE would answer for a region the box does not cover, and that
    // region's max can be FARTHER than the box's -- the one direction that culls something visible.
    if (lo.x < -1.0 || lo.y < -1.0 || hi.x > 1.0 || hi.y > 1.0) return false;
    vec2 uv0 = lo * 0.5 + 0.5, uv1 = hi * 0.5 + 0.5;
    vec2 px = (uv1 - uv0) * uPyrSize;
    // A level whose neighbourhood covers the box. Rounding UP over-covers, which only ever raises
    // the max it compares against, so it errs toward drawing.
    int lod = int(clamp(ceil(log2(max(max(px.x, px.y), 1.0))), 0.0, uMaxLod));
    float d = max(max(CxPyr(uv0, lod), CxPyr(vec2(uv1.x, uv0.y), lod)),
                  max(CxPyr(vec2(uv0.x, uv1.y), lod), CxPyr(uv1, lod)));
    return near * 0.5 + 0.5 > d;
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uDrawN)) return;
    uint b = i * 6u;
    uint batch = uSrc[b + 4u];
    // #290: a batch at or above uCompactBelow is SORTED -- this dispatch only decides its verdict
    // and measures its key, and the second dispatch places it. Everything below compacts here, as
    // it has since #243, because opaque draws have no order to preserve.
    bool sorted = batch >= uint(uCompactBelow);
    // #489: phase two is the marked rejects alone. Sorted batches are never re-tested because phase
    // one never hi-Z tests them (below), so a mark on one cannot exist.
    if (uRej == 2 && (sorted || uRejBuf[i] == 0u)) return;
    int slot = int(uSrc[b + 3u]);
    vec4 s = uSph[slot];
    bool keep = true;
    bool hiz = false;
    if (s.w < 1e29)
    {
        for (int p = 0; p < 6 && keep; p++)
            if (dot(uPlanes[p].xyz, s.xyz) + uPlanes[p].w < -s.w) keep = false;
        // #290: LOD is for the OPAQUE range alone. A blended or additive draw is routinely far
        // larger on screen than its bounding radius -- a torch flame or a spell mote is a bloomed
        // sprite, and thinning it by projected radius would delete lights the frustum and the
        // pyramid both agree are visible.
        if (keep && !sorted && CxSubPixel(s.xyz, s.w)) keep = false;
        if (s.z + s.w < uClipZ) keep = false;
        // #489: a SORTED draw is not tested against the stale pyramid. The blended prepass draws
        // phase one's placement of it as a creature's self-depth layer, and phase two does not
        // restore sorted draws -- so a stale rejection there would be the one-frame pop this exists
        // to prevent.
        if (keep && uHiZ != 0 && !(sorted && uRej == 1) && CxOccluded(s.xyz, s.w)) { keep = false; hiz = true; }
        // #341: past the fog's opaque distance AND entirely below the eye -- see CullPass.FogCull
        // for why both. The distance is the fog's own metric (CxFogFactor), and the radius is
        // subtracted so a sphere reaching back inside the fog is kept whole.
        if (keep && uFogCull > 0.0 && s.z + s.w < uCullEye.z)
        {
            float d = uFogOrigin.w > 0.0 ? length(s.xyz - uFogOrigin.xyz) : (uVP * vec4(s.xyz, 1.0)).w;
            if (d - s.w > uFogCull) keep = false;
        }
    }
    // #489: the mark is "rejected by the stale hi-Z and nothing else" -- a frustum, LOD or fog
    // verdict is same-frame and needs no second look.
    if (uRej == 1) uRejBuf[i] = hiz ? 1u : 0u;
    if (sorted)
    {
        // The key, and the verdict carried in its sign: squared distance from this pass's eye,
        // which is exactly what the CPU's EyeDist2 measures, so the two orders agree.
        vec3 d = s.xyz - uCullEye.xyz;
        uKey[i] = keep ? dot(d, d) : -1.0;
        // The count is the batch's SURVIVOR count -- the sort compacts as well as orders.
        if (keep) atomicAdd(uCnt[batch], 1u);
        return;
    }
    if (!keep) return;
    uint o = uSrc[b + 5u] + atomicAdd(uCnt[batch], 1u);
    uint c = o * 5u;
    uCmd[c] = uSrc[b];        // count
    uCmd[c + 1u] = 1u;        // instanceCount
    uCmd[c + 2u] = uSrc[b + 1u];   // firstIndex
    uCmd[c + 3u] = uSrc[b + 2u];   // baseVertex
    uCmd[c + 4u] = 0u;        // baseInstance
    uOut[o] = slot;
}

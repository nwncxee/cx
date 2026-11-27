// Lays cx_airdust_cs's air over the deferred frame at full resolution:
//     hdr.rgb = hdr.rgb * keep + lit
// (lit, keep) comes off the four blocks nearest each pixel, each by its bilinear share and by how near the depth its
// ray was marched at lies to the pixel's own, so the air over a far wall does not haze a pillar in front of it.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

#include "cx_defblk.glsl"

layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
layout(binding = 8) uniform sampler2D uAirDust;

/// The side of the block of the main view one of cx_airdust_cs's rays stands for, pixels: AirScene.DustDiv, defined by
/// the build; an override built without it takes 4.
#ifndef CX_AIR_DIV
#define CX_AIR_DIV 4
#endif
/// Depth difference, as a share of the farther of two depths, past which a block has no say over a pixel.
const float CX_AIR_EDGE = 0.04;

/// The air at a pixel of depth z off the four blocks around it, t0..t3 (light, keep) marched at depths zq, a fraction f
/// across them: a block at another depth has no say, and where every block is at another, the nearest in depth stands.
vec4 CxAirUp(float z, vec4 zq, vec2 f, vec4 t0, vec4 t1, vec4 t2, vec4 t3)
{
    vec4 rel = abs(zq - z) / max(max(zq, vec4(z)), vec4(1e-6));
    vec4 w = vec4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y) * max(1.0 - rel / CX_AIR_EDGE, 0.0);
    float sum = w.x + w.y + w.z + w.w;
    if (sum > 1e-4) return (w.x * t0 + w.y * t1 + w.z * t2 + w.w * t3) / sum;
    float m = min(min(rel.x, rel.y), min(rel.z, rel.w));
    return m == rel.x ? t0 : m == rel.y ? t1 : m == rel.z ? t2 : t3;
}

/// The depth block q's ray was marched at: the depth under its middle pixel.
float CxAirUpDepth(ivec2 q)
{
    return texelFetch(uGbDepth, min(q * CX_AIR_DIV + CX_AIR_DIV / 2, uRect.zw - 1) + uRect.xy, 0).r;
}

void main()
{
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(id, uRect.zw))) return;
    ivec2 last = textureSize(uAirDust, 0) - 1;
    vec2 u = (vec2(id) - float(CX_AIR_DIV / 2)) / float(CX_AIR_DIV);
    ivec2 b = ivec2(floor(u));
    ivec2 q0 = clamp(b, ivec2(0), last), q1 = clamp(b + 1, ivec2(0), last);
    ivec2 q2 = ivec2(q1.x, q0.y), q3 = ivec2(q0.x, q1.y);
    vec4 t0 = texelFetch(uAirDust, q0, 0), t1 = texelFetch(uAirDust, q2, 0), t2 = texelFetch(uAirDust, q3, 0), t3 = texelFetch(uAirDust, q1, 0);
    if (min(min(t0.a, t1.a), min(t2.a, t3.a)) >= 1.0) return;
    ivec2 p = id + uRect.xy;
    float z = texelFetch(uGbDepth, p, 0).r;
    vec4 zq = vec4(CxAirUpDepth(q0), CxAirUpDepth(q2), CxAirUpDepth(q3), CxAirUpDepth(q1));
    vec4 air = CxAirUp(z, zq, u - vec2(b), t0, t1, t2, t3);
    vec4 hdr = imageLoad(uHdr, p);
    imageStore(uHdr, p, vec4(hdr.rgb * air.a + air.rgb, hdr.a));
}

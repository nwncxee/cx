// Takes the half-resolution mover debt out of the deferred frame at full resolution:
//     hdr.rgb -= lmShare * owe
// owe comes off the four nearest cx_moverdebt texels, each weighted by how near its depth lies to this pixel's, so a
// shadow on the ground does not bleed onto a creature or a wall in front of it.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;

#include "cx_pass.glsl"
#include "cx_chartbuf.glsl"
#include "cx_gbuf.glsl"

layout(binding = 3) uniform usampler2D uGb0;
layout(binding = 4) uniform usampler2D uGb1;
layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
layout(rgba32ui, binding = 1) uniform uimage2D uLightRec;   // cx_gbuf's light record: this pass adds its debt
#include "cx_defblk.glsl"

layout(binding = 8) uniform sampler2D uMoverOcc;

/// Depth difference, as a share of the pixel's view depth, past which a half-resolution texel has no say.
const float CX_DEBT_DEPTH = 0.02;

void main()
{
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(id, uRect.zw))) return;
    ivec2 q = id >> 1, s = (id & 1) * 2 - 1;
    ivec2 last = textureSize(uMoverOcc, 0) - 1;
    vec2 t0 = texelFetch(uMoverOcc, q, 0).rg;
    vec2 t1 = texelFetch(uMoverOcc, clamp(q + ivec2(s.x, 0), ivec2(0), last), 0).rg;
    vec2 t2 = texelFetch(uMoverOcc, clamp(q + ivec2(0, s.y), ivec2(0), last), 0).rg;
    vec2 t3 = texelFetch(uMoverOcc, clamp(q + s, ivec2(0), last), 0).rg;
    if (t0.r + t1.r + t2.r + t3.r <= 0.0) return;

    ivec2 p = id + uRect.xy;
    float z = texelFetch(uGbDepth, p, 0).r;
    if (!(z > 0.0)) return;
    float viewDepth = CxDepthViewZ(z, uProj);
    float cut = 1.0 / (CX_DEBT_DEPTH * viewDepth);
    vec4 dw = max(1.0 - abs(vec4(t0.g, t1.g, t2.g, t3.g) - viewDepth) * cut, vec4(0.0));
    vec4 w = vec4(0.5625, 0.1875, 0.1875, 0.0625) * dw;
    float sum = w.x + w.y + w.z + w.w;
    if (!(sum > 0.0)) return;
    float owe = dot(w, vec4(t0.r, t1.r, t2.r, t3.r)) / sum;
    if (!(owe > 0.0)) return;

    uvec4 g1 = texelFetch(uGb1, p, 0);
    CxGbuf g;
    if (!CxGbufReadWalk(texelFetch(uGb0, p, 0), g1.w, viewDepth, uGbDepth, p, uProj, g)) return;
    if ((g.flags & CX_GB_BAKED) == 0u) return;
    CxGbufWeights(g1, g);
    uvec4 lr = imageLoad(uLightRec, p);
    vec2 rec = unpackHalf2x16(lr.w);
    if (rec.x != 0.0) imageStore(uLightRec, p, uvec4(lr.xyz, packHalf2x16(vec2(rec.x, min(rec.y + owe, 1.0)))));
    vec4 hdr = imageLoad(uHdr, p);
    // alpha is the probe capture's 1 / distance, which this pass never runs for: hand it back untouched
    imageStore(uHdr, p, vec4(hdr.rgb - g.lmShare * owe, hdr.a));
}

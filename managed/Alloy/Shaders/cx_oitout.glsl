#ifdef CX_DECAL_WRITE
// the decal layer's writer: location 1 is its surface pair (prepass depth, decal view depth), never revealage
layout(location = 1) out vec2 oDecalSurf;
float oReveal;
#elif defined(CX_DEPTH_ONLY)
// CX-303: a content pair's depth twin writes the prepass's vis-ID on location 1, as the plain depth stages do
#include "cx_idout.glsl"
float oReveal;
#else
layout(location = 1) out float oReveal;
#endif
uniform int uOit;
uniform sampler2D uOitDepth;
// The lists block is compiled in ONLY for the OIT bracket's twin program (OitPass.WithLists): an SSBO
// atomic in a fragment stage is a side effect, and the driver then runs every fragment before the depth
// test -- even down a dead uOit branch -- which costs the opaque pass its early-Z (measured 5x on NVIDIA).
#ifdef CX_OIT_LISTS
// ...so the twin forces the GL depth test (the scene depth, attached read-only) ahead of the shader: a hidden
// fragment is rejected before it runs, and the append and its atomics never see it
layout(early_fragment_tests) in;
layout(std430, binding = 4) buffer CxOitHeads { uint uOitCount; uint uOitHead[]; };
layout(std430, binding = 5) writeonly buffer CxOitNodes { uvec4 uOitNode[]; };
#endif
uniform ivec2 uOitDim;   // (head-table stride in pixels, node capacity)
uniform int uOitGob;     // the batch's object id (1..255, 0 = none): one object composites as ONE layer
vec4 CxOitOut(vec4 c)
{
    if (uOit == 0) { oReveal = 1.0; return c; }
    float a = clamp(c.a, 0.0, 1.0);
    ivec2 px = ivec2(gl_FragCoord.xy);
#ifdef CX_OIT_LISTS
    if (uOit == 2)
    {
        uint i = atomicAdd(uOitCount, 1u) + 1u;   // 1-based: 0 is the empty head after the clear
        if (i <= uint(uOitDim.y))
        {
            uint prev = atomicExchange(uOitHead[px.y * uOitDim.x + px.x], i);
            // 24-bit depth over an 8-bit object id: the resolve sorts on the whole word, depth first
            uint key = (uint(clamp(gl_FragCoord.z, 0.0, 1.0) * 16777215.0) << 8) | uint(uOitGob);
            uOitNode[i] = uvec4(prev, key, packHalf2x16(c.rg), packHalf2x16(vec2(c.b, a)));
        }
    }
#endif
    // McGuire & Bavoil eq. 9: near-and-opaque weighs most; clamped so fp16 neither under- nor overflows.
    float w = clamp(pow(min(1.0, a * 10.0) + 0.01, 3.0) * 1e8 * pow(1.0 - gl_FragCoord.z * 0.9, 3.0), 1e-2, 3e3);
    oReveal = a;
    return vec4(c.rgb * a, a) * w;
}

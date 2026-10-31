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
uniform ivec2 uOitDim;   // (head-table stride in pixels, node capacity)
uniform int uOitGob;     // the batch's object id (1..255, 0 = none): one object composites as ONE layer
vec4 CxOitOut(vec4 c)
{
    if (uOit == 0) { oReveal = 1.0; return c; }
    float a = clamp(c.a, 0.0, 1.0);
    ivec2 px = ivec2(gl_FragCoord.xy);
    // McGuire & Bavoil eq. 9: near-and-opaque weighs most; clamped so fp16 neither under- nor overflows.
    float w = clamp(pow(min(1.0, a * 10.0) + 0.01, 3.0) * 1e8 * pow(0.1 + gl_FragCoord.z * 0.9, 3.0), 1e-2, 3e3);
    oReveal = a;
    return vec4(c.rgb * a, a) * w;
}

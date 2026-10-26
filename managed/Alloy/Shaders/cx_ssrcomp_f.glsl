#version 460 core
// CX-51: Ssr's half-res trace onto the full-res frame, added under ONE/ONE (a viz replaces). Each pixel joins the
// four traced texels around it that sat at its own depth, and scales them by its OWN record's weight and gate: the
// trace carries (screen - probe) x trust, never where a reflection belongs, so none bleeds off a puddle's edge.
#include "cx_ssrrec.glsl"
layout(location = 0) out vec4 frag;
uniform sampler2D uSrc;      // the trace: rgb = (screen - probe) x trust, a = the traced pixel's 1 / view depth (0 = none)
uniform sampler2D uRecN;
uniform sampler2D uRecW;
uniform sampler2D uHiZ;      // level 0: 1 / view depth per full-res pixel
uniform float uCap;          // the roughness cap
uniform int uViz;
/// How far (a share of view depth) a traced texel may sit off this pixel and still stand for it, faded over the range.
const vec2 CX_SSR_JOIN = vec2(0.01, 0.03);
void main()
{
    ivec2 px = ivec2(gl_FragCoord.xy), lim = textureSize(uSrc, 0) - 1;
    if (uViz != 0) { frag = texelFetch(uSrc, min(px / 2, lim), 0); return; }
    frag = vec4(0.0);
    vec3 n, w; float rough;
    if (!CxRecRead(uRecN, uRecW, uHiZ, px, n, rough, w)) return;
    float gate = CxSsrGate(max(w.r, max(w.g, w.b)), rough, uCap);
    if (gate <= 0.0) return;
    float k = texelFetch(uHiZ, px, 0).r;
    vec2 pos = (vec2(px) + 0.5) * 0.5 - 0.5, f = fract(pos);
    ivec2 i0 = ivec2(floor(pos));
    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    for (int j = 0; j < 4; j++)
    {
        ivec2 o = ivec2(j & 1, j >> 1);
        vec4 s = texelFetch(uSrc, clamp(i0 + o, ivec2(0), lim), 0);
        float bil = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
        float wt = bil * (1.0 - smoothstep(CX_SSR_JOIN.x, CX_SSR_JOIN.y, abs(s.a - k) / k));
        sum += s.rgb * wt;
        wsum += wt;
    }
    if (wsum < 1e-3) return;
    frag = vec4(sum / wsum * (gate * clamp(w, 0.0, 1.0)), 0.0);
    if (any(isnan(frag)) || any(isinf(frag))) frag = vec4(0.0);
}

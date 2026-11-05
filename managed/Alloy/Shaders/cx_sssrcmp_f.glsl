#version 460 core
// The SSSR trial's composite: AMD's denoised reflection, its misses already the probe, turned into the correction the lit
// stage's probe term needs, (reflection - probe) x this pixel's weight and gate, as cx_ssrres_f writes it.
#include "cx_ssr.glsl"
#include "cx_ssrrec.glsl"
layout(location = 0) out vec4 frag;
uniform sampler2D uSssr;
uniform sampler2D uDepth;
uniform sampler2D uHiZ;
uniform sampler2D uRecN;
uniform sampler2D uRecW;
uniform samplerCube uProbe;
uniform mat4 uInvProj, uView, uInvView;
uniform float uCap;
uniform float uProbeMips;
/// As cx_ssrres_f's: the frame is lit without the moving lights the probe saw, so a dark hit stands for a darker reflection.
const float CX_SSR_FLOOR = 0.25;

void main()
{
    frag = vec4(0.0);
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec3 n, w; float rough;
    if (!CxRecRead(uRecN, uRecW, uHiZ, px, n, rough, w)) return;
    float gate = CxSsrGate(max(w.r, max(w.g, w.b)), rough, uCap);
    if (gate <= 0.0) return;
    vec2 uv = (vec2(px) + 0.5) / vec2(textureSize(uDepth, 0));
    vec3 o = CxViewPos(uInvProj, uv, texelFetch(uDepth, px, 0).r);
    vec3 N = normalize(mat3(uView) * n), V = -normalize(o);
    vec3 P = (uInvView * vec4(o, 1.0)).xyz, R = mat3(uInvView) * reflect(-V, N);
    vec3 env = uProbeMips > 0.0 ? CxProbeFetch(uProbe, P, R, sqrt(max(rough, 1e-3)) * (uProbeMips - 1.0)) : vec3(0.0);
    vec3 s = min(texelFetch(uSssr, px, 0).rgb, vec3(CX_SSR_MAX));
    if (any(isnan(s))) return;
    frag = vec4((max(s, env * CX_SSR_FLOOR) - env) * (gate * clamp(w, 0.0, 1.0)), 0.0);
    if (any(isnan(frag)) || any(isinf(frag))) frag = vec4(0.0);
}

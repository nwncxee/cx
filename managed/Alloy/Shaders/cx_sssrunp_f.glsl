#version 460 core
// The SSSR trial's input: AMD reads a normal through a multiply and an add, which cannot undo the record's octahedron.
// rgb = the world normal, a = alpha roughness, with 1 wherever Alloy's pass would trace nothing. A pixel with no record (a
// later draw covered it) takes its normal from depth, so a ray can still hit it from the front.
#include "cx_ssr.glsl"
#include "cx_ssrrec.glsl"
layout(location = 0) out vec4 frag;
uniform sampler2D uDepth;
uniform sampler2D uHiZ;
uniform sampler2D uRecN;
uniform sampler2D uRecW;
uniform mat4 uInvProj, uInvView;
uniform float uCap;

vec3 At(ivec2 p, vec2 size)
{
    return CxViewPos(uInvProj, (vec2(p) + 0.5) / size, texelFetch(uDepth, p, 0).r);
}

void main()
{
    ivec2 px = ivec2(gl_FragCoord.xy), lim = textureSize(uDepth, 0) - 1;
    vec2 size = vec2(lim + 1);
    vec3 n, w; float rough;
    if (CxRecRead(uRecN, uRecW, uHiZ, px, n, rough, w))
    {
        if (CxSsrGate(max(w.r, max(w.g, w.b)), rough, uCap) <= 0.0) rough = 1.0;
        frag = vec4(n, clamp(rough, 0.0, 1.0));
        return;
    }
    frag = vec4(0.0, 0.0, 1.0, 1.0);
    if (!(texelFetch(uDepth, px, 0).r > 0.0)) return;
    vec3 p = At(px, size);
    vec3 xa = At(min(px + ivec2(1, 0), lim), size) - p, xb = p - At(max(px - ivec2(1, 0), ivec2(0)), size);
    vec3 ya = At(min(px + ivec2(0, 1), lim), size) - p, yb = p - At(max(px - ivec2(0, 1), ivec2(0)), size);
    vec3 v = cross(abs(xa.z) < abs(xb.z) ? xa : xb, abs(ya.z) < abs(yb.z) ? ya : yb);
    if (dot(v, v) < 1e-12 || any(isnan(v))) return;
    v = normalize(v);
    if (dot(v, -p) < 0.0) v = -v;
    frag.xyz = normalize(mat3(uInvView) * v);
}

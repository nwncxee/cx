#version 460 core
// The precipitation's velocity: each streak or flake's own fall over the ground's vector, in its core alone.
in vec2 vUV;
in float vSeed;
in vec2 vVel;
out vec2 frag;
uniform int uKind;
#include "cx_wx_motion.glsl"
void main()
{
    float a;
    if (uKind == 2) { float r = length(vUV); a = r >= 1.0 ? 0.0 : smoothstep(1.0, 0.5, r); }
    else { a = (1.0 - vUV.x * vUV.x) * (1.0 - vUV.y * vUV.y); a = a * a; }
    float cov = a * vSeed;
    // the edge writes nothing, so the vector the ground already wrote there stays
    if (cov <= CX_WX_CORE) discard;
    frag = CxWxVelocity(cov, vVel, vec2(0.0));
}

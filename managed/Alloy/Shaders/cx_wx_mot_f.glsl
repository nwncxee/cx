#version 460 core
// The precipitation's velocity: each streak or flake's own fall over the ground's vector, in its core alone.
in vec2 vUV;
in float vSeed;
in float vViewZ;
in vec2 vVel;
out vec2 frag;
uniform int uKind;
uniform float uAlpha;
uniform float uSoft;
uniform mat4 uProj;
uniform sampler2D uSceneDepth;
#include "cx_depthlin.glsl"
#include "cx_wx_shade.glsl"
#include "cx_wx_motion.glsl"
void main()
{
    float gap = CxDepthViewZ(texture(uSceneDepth, gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))).r, uProj) + vViewZ;
    float cov = CxWxCover(uKind, vUV, clamp(gap * uSoft, 0.0, 1.0), vSeed) * uAlpha;
    // the edge writes nothing, so the vector the ground already wrote there stays
    if (cov <= CX_WX_CORE) discard;
    frag = CxWxVelocity(cov, vVel, vec2(0.0));
}

#version 460 core
// The volumetric fog over a bare-sky render texel, laid before FSR so the fog rides FSR's own history: the blend
// multiplies the texel by the share of the far light the fog keeps (alpha) and adds the light it scatters in (colour).
in vec2 vUV;
out vec4 frag;
uniform sampler2D uDepth;
uniform vec2 uJitterUv;   // this frame's jitter in UV: a render texel holds the scene that far behind its centre
#include "cx_fogdecl.glsl"
#include "cx_vfog.glsl"

void main()
{
    if (!CxVFogOn() || texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r > 0.0) discard;
    frag = CxVFogSkyUv(vUV - uJitterUv);
}

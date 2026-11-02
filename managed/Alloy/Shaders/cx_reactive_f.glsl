#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uSrc;    // the scene colour as the beam, particle, halo and weather passes left it
uniform sampler2D uCopy;   // the same colour as it stood before those four passes
uniform float uExposure;   // the resolve's scene-referred gain, applied before the curve as it applies it
uniform int uMode;
uniform float uKnee;
uniform float uAcesMix;
#include "cx_tonemap.glsl"

// How reactive FSR and the TAA resolve treat this pixel: the change those four passes made to its
// TONEMAPPED colour, four times over and capped below 1. Four times because a streak that covers a
// tenth of a pixel still has no history worth reprojecting; the cap because a fully reactive pixel is
// one frame of noise with nothing averaging it.
float CxReactiveMask(vec3 toneFinal, vec3 toneCopy)
{
    vec3 d = abs(toneFinal - toneCopy);
    return min(max(max(d.r, d.g), d.b) * 4.0, 0.9);
}

void main()
{
    // the bloom the resolve adds is computed from the finished frame, so it is not here to add: this
    // measures the passes' own contribution, which is what the history has no record of
    vec3 fin = CxTonemap(textureLod(uSrc, vUV, 0.0).rgb * uExposure, uMode, uKnee, uAcesMix);
    vec3 was = CxTonemap(textureLod(uCopy, vUV, 0.0).rgb * uExposure, uMode, uKnee, uAcesMix);
    frag = vec4(CxReactiveMask(fin, was), 0.0, 0.0, 1.0);
}

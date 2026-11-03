#version 460 core
in vec2 vUV;
in float vViewZ;
in float vSeed;
in vec3 vLight;
in vec3 vBehind;
in vec3 vSkyLow;
in vec3 vLampDir;
in float vSunVis;
in vec3 vSide;
in vec3 vUp;
uniform int uKind;
uniform vec3 uTint;
uniform float uAlpha;
uniform float uSoft;
uniform vec2 uScreen;    // the target being drawn, pixels: the depth copy is sampled across it whatever its own size
// uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
#include "cx_scenedepth.glsl"
#include "cx_wx_shade.glsl"
#include "cx_wx_drop.glsl"
void main()
{
    if (CxWxCover(uKind, vUV, 1.0, 1.0) <= 0.01) discard;
    float gap = CxSceneViewZ(gl_FragCoord.xy / uScreen) + vViewZ;
    float k = clamp(gap * uSoft, 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
    // the quad faces the camera, so its two axes span the view and their cross product is the way back to the eye
    vec3 V = normalize(cross(vSide, vUp));
    vec4 bead = CxWxBead(vUV, V, vSide, vUp, vBehind, vSkyLow, vLampDir, uTint * vLight, uAreaDir, uTint * uAreaDiffuse * vSunVis);
    frag = vec4(bead.rgb, bead.a * uAlpha) * CxWxCover(uKind, vUV, k, vSeed);
}

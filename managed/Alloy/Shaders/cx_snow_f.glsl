#version 460 core
in vec2 vUV;
in float vViewZ;
in float vSeed;
in vec3 vLight;
in vec3 vBehind;
uniform int uKind;
uniform vec3 uTint;
uniform float uAlpha;
uniform float uSoft;
uniform vec2 uScreen;    // the target being drawn, pixels: the depth copy is sampled across it whatever its own size
#include "cx_scenedepth.glsl"
#include "cx_wx_shade.glsl"
#include "cx_reactive.glsl"
void main()
{
    fragReact = 0.0;
    if (CxWxCover(uKind, vUV, 1.0, 1.0) <= 0.01) discard;
    float gap = CxSceneViewZ(gl_FragCoord.xy / uScreen) + vViewZ;
    float k = clamp(gap * uSoft, 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
    frag = CxWxShade(uKind, CxWxCover(uKind, vUV, k, vSeed) * uAlpha, uTint * vLight, vBehind);
    fragReact = CxReactive(CxReactiveShare(frag.rgb, frag.a, false));
}

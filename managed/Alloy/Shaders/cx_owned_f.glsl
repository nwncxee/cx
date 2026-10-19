#version 460 core
in vec2 vUV;
flat in float vAlpha;
in vec3 vWorld;
uniform sampler2D uTex;
uniform float uLodBias;        // #391
uniform int uViz;
// The sky draws set this to the horizon band's width in ray-elevation (dir.z): every ray at or
// below the horizon is FULLY fogged, fading to the raw dome by that much above it. 0 = an ordinary
// unlit part, which is never fogged. Culled ground reveals the dome's lower half, and this is what
// makes that half the exact colour the ground had converged to (CullPass.FogCull).
uniform float uSkyFog;
// #126: the engine's fAlphaDiscardValue -- 0.2 on the classes it draws under Enable(1), -1.0 (i.e.
// no test at all) on the rest. A uniform here because the class is a per-batch fact.
uniform float uAlphaCut;
// #222: the constant colour PartTriMesh::Mat sets before the draw. White for every ordinary unlit
// part; the SCENE'S FOG COLOUR for a part whose gob carries the +0x19 tint byte, which is what the
// skyfade gob is. A uniform rather than a per-draw record field because the whole unlit class shares
// one value and the sky changes it at most once per role.
uniform vec3 uTint;
layout(location = 0) out vec4 frag;
#include "cx_fogdecl.glsl"
#include "cx_fogmix.glsl"
#include "cx_oitout.glsl"
#include "cx_alphamip.glsl"
void main()
{
    vec2 du1 = dFdx(vUV), du2 = dFdy(vUV);
    if (uViz == 4) { frag = vec4(1.0, 0.0, 1.0, 1.0); return; }
    vec4 t = texture(uTex, vUV, uLodBias);
    if (uAlphaCut >= 0.0 && CxAlphaMip(t.a, du1, du2, vec2(textureSize(uTex, 0)), uLodBias) <= uAlphaCut) discard;
    // #132: the target is LINEAR, so the texel's display-space encode comes off here too -- the
    // probe and the frame want the same thing now. Same exponent AlloyLights.CxLinear uses,
    // spelled out because this stage does not include that lib.
    vec3 tinted = t.rgb * uTint;
    vec3 rgb = pow(max(tinted, vec3(0.0)), vec3($SRGB$));
    // the sky's horizon band only -- an unlit PART is never fogged (vanilla compiles it FOG 0)
    if (uSkyFog > 0.0) rgb = CxFogSky(rgb, vWorld, uSkyFog);
    frag = CxOitOut(vec4(rgb, t.a * vAlpha));
}

#version 460 core
#define CX_FS 1   // #642: gl_FragCoord is in reach for the shadow lib's dither
#define CX_LIGHT_LITE 1   // CX-188: cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light
in vec4 vColor;
in vec2 vUv;
in float vViewZ;
in vec3 vWorld;
in vec3 vNrm;
in float vSoft;
in vec3 vLight;
flat in int vType;

uniform sampler2D uTex;
uniform float uAlphaCut;    // fAlphaDiscardValue: 0.2 punch-through, -1.0 (off) otherwise
uniform int uAdditive;
uniform float uEmissive;    // .mtr fEmissive: >1 pushes the effect into #139's HDR headroom
uniform int uSoftOn;        // 0 = no depth copy this pass, so no fade
// uViz: /particles viz: 1 = tint by render type, 2 = flat, no texture. #336: the light rig's terms
// are the VERTEX stage's now; this stage only applies vLight. The libraries still compose in below
// for CxLinear.
#ifdef CX_MOTION
in vec4 vCur;
in vec4 vPrev;
// a sprite whose share of the pixel is below this leaves it mostly background, which keeps its own vector
const float CX_MOTION_CUT = 0.5;
#include "cx_motionout.glsl"
#endif
#include "cx_scenedepth.glsl"
#include "cx_fogdecl.glsl"

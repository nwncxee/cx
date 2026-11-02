#version 460 core
#ifdef CX_WX_WAVE
#extension GL_KHR_shader_subgroup_ballot : require
#endif
// The precipitation cull: one lane per lattice point. It places the drop, keeps it only when it is on screen, in the
// thinned field and under open sky (or, for a roof drip, under a lip), lights it once, and appends the survivors to
// the records the vertex stage reads by instance, counting them into the indirect draw's command.
layout(local_size_x = 64) in;
#include "cx_wx_uni.glsl"
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
layout(std430, binding = 14) coherent buffer CxWxCmd { uint uCmd[]; };
/// Past the two draw commands: the survivors each command wanted before the record cap, for the cap's log line.
const int CX_WX_WANTED = 8;
layout(std430, binding = 15) writeonly buffer CxWxDropsOut { vec4 uDropsOut[]; };
// CX_WX_REC_CAP, the most survivors one command keeps, is defined by the pass that builds this stage.
/// The forward lobe of a rain drop's sun scatter (Henyey-Greenstein g).
const float CX_WX_RAIN_G = 0.35;

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
#define CX_LIGHT_LITE 1   // cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light
#include "cx_wx_hash.glsl"
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"
#include "cx_sunshadow.glsl"
#include "cx_probeshape.glsl"
#define CX_WX_LAMPS 1
#include "cx_wx_shade.glsl"
#include "cx_vfrain.glsl"
#include "cx_wx_cullmain.glsl"

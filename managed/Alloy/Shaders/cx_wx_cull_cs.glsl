#version 460 core
#ifdef CX_WX_WAVE
#extension GL_KHR_shader_subgroup_ballot : require
#endif
// The precipitation cull: one lane per lattice point. It places the drop, keeps it only when it is on screen, in the
// thinned field and under open sky (or, for a roof drip, under a lip), lights it once, and appends the survivors to
// the records the vertex stage reads by instance, counting them into the indirect draw's command.
layout(local_size_x = 64) in;
layout(std430, binding = 14) coherent buffer CxWxCmd { uint uCmd[]; };
/// Past the two draw commands: the survivors each command wanted before the record cap, for the cap's log line.
const int CX_WX_WANTED = 8;
layout(std430, binding = 15) writeonly buffer CxWxDropsOut { vec4 uDropsOut[]; };
// CX_WX_REC_CAP, the most survivors one command keeps, is defined by the pass that builds this stage.
#include "cx_wx_place.glsl"
#include "cx_wx_cullmain.glsl"

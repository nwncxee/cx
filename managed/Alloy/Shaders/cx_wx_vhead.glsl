#version 460 core
#include "cx_wx_uni.glsl"
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
layout(std430, binding = 15) readonly buffer CxWxDrops { vec4 uDrops[]; };
#ifdef CX_MOTION
uniform mat4 uVp;        // this frame's view-projection, jittered as the frame drew
uniform mat4 uPrevVP;    // last frame's, unjittered
#include "cx_motionout.glsl"
out vec2 vVel;           // the drop's own motion, render pixels
#endif
out vec2 vUV;
out float vViewZ;
out float vSeed;
out vec3 vLight;         // linear light at this drop or flake
out vec3 vBehind;        // linear sky seen through it

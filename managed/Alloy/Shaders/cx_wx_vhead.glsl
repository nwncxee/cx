#version 460 core
#include "cx_wx_uni.glsl"
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
layout(std430, binding = 15) readonly buffer CxWxDrops { vec4 uDrops[]; };
out vec2 vUV;
out float vViewZ;
out float vSeed;
out vec3 vLight;         // linear light at this drop or flake
out vec3 vBehind;        // linear sky seen through it

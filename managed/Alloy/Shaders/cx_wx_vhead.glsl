#version 460 core
#include "cx_wx_uni.glsl"
// uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
layout(std430, binding = 15) readonly buffer CxWxDrops { vec4 uDrops[]; };
#ifdef CX_MOTION
uniform mat4 uVp;        // this frame's view-projection, jittered as the frame drew
uniform mat4 uPrevVP;    // last frame's, unjittered
#include "cx_motionout.glsl"
out vec2 vVel;           // the drop's own motion, render pixels
#endif
#include "cx_wx_quad.glsl"
out vec2 vUV;
out float vViewZ;
out float vSeed;
out vec3 vLight;         // linear light at this flake, or the lamps on this drop
out vec3 vBehind;        // linear sky through this flake, or over this drop
out vec3 vSkyLow;        // linear sky at the horizon behind this drop
out vec3 vLampDir;       // toward the lamps lighting this drop
out float vSunVis;       // the sun's visibility at this drop
out vec3 vSide;          // the quad's across axis
out vec3 vUp;            // the quad's along axis

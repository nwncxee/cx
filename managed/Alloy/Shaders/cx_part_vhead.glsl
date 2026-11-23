#version 460 core
#include "cx_partrec.glsl"
layout(std430, binding = 15) readonly buffer CxParticleIdx { uint partIdx[]; };

uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uEye;
#ifdef CX_MOTION
uniform mat4 uPrevVP;       // CX-48: last frame's view-projection, unjittered
out vec4 vCur;              // this vertex's clip position, and last frame's
out vec4 vPrev;
#endif

out vec4 vColor;
out vec2 vUv;
out float vViewZ;
out vec3 vWorld;
out vec3 vNrm;
out float vSoft;
out vec3 vLight;            // #336: CxPartLight of ambient + sun + point at this vertex, never under 1; 1.0 when unlit
flat out int vType;

uniform int uLit;           // 0 = this draw is self-illuminated (additive, punch-through) or unlit
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
#define CX_LIGHT_LITE 1   // CX-188: cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light

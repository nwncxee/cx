// CX-24: the procedural blade vertex head. A blade is a triangle strip read off the scatter's record by
// gl_VertexID: 2*uSegments+1 vertices, root pairs up to a single tip vertex.
#version 460 core
// the velocity pass redraws every blade and keeps only the fragments whose depth equals the colour
// pass's: two programs over this one text, so the clip position must come out bit-identical from both
invariant gl_Position;
layout(location = 2) in vec4 iPos;      // xyz world root, w facing (radians about Z)
layout(location = 3) in vec4 iParam;    // x height, y material*4 + cell, z rank, w bend
layout(location = 4) in vec4 iNrm;      // xyz the ground's normal, w fade (1 = whole, 0 = the ground's colour)
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
#include "cx_gbladebuf.glsl"
#include "cx_gsway.glsl"
uniform mat4 uVP;
uniform vec4 uLod;             // x full-density radius, y keep floor, z width exponent, w height exponent
uniform vec2 uSnow;            // y = winter look
uniform float uWindGain;
uniform float uWindSpeed;
uniform sampler2D uPushTex;
uniform float uPushGain;
uniform vec3 uPushWindow;
uniform int uSegments;         // strip pairs: 7 near, 1 far
uniform float uWidth;          // blade width at the root, as a share of its height
uniform int uVLit;
uniform int uCast;
uniform float uCastWidth;
uniform int uLit;
uniform int uGroundOn;         // 1 = the fade goes out through the ground blend; 0 = the blade shrinks instead
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
out vec3 vLight;
out vec3 vWorld;
out vec3 vNrm;
out float vDepth;
out float vDist;
out float vRoot;
out float vLookDown;
flat out float vFade;
flat out vec3 vColRoot;
flat out vec3 vColMid;
flat out vec3 vColTip;
flat out vec3 vMatDif;
flat out vec3 vMatAmb;

float CxGrassKeep(float d)
{
    if (d <= uLod.x) return 1.0;
    float r = uLod.x / d;
    return max(uLod.y, r * r);
}

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
#define CX_LIGHT_LITE 1   // CX-188: cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light

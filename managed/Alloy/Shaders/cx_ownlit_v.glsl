#version 460 core
#ifdef CX_CAST_LAYER
#extension GL_ARB_shader_viewport_layer_array : require
#endif
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec3 aNrm;
// #674: the lightmap chart UV, in the mesh atlas's texel units (MeshChart.LocChartUv). Only a charted
// VAO enables the array; every other reads the constant attribute, and its record says unbaked anyway.
layout(location = 3) in vec2 aChart;
// CX-221: the mesh arena's two packed words. aTangent is INT_2_10_10_10_REV -- xyz the model-space tangent,
// w the handedness -- and a ZERO w is "this mesh carries no frame", which is what sends the fragment stage
// back to its derivative frame. aPaint is the mesh's normalized RGBA8 vertex paint, white where it has none.
layout(location = 4) in vec4 aTangent;
layout(location = 5) in vec4 aPaint;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
uniform mat4 uVP;
uniform int uDrawBase;
invariant gl_Position;
out vec2 vUV;
out vec3 vWorld;
out vec3 vNrm;
out vec4 vTan;                 // CX-221: world tangent xyz, handedness w (0 = the mesh supplied no frame)
out float vDepth;
out vec2 vChart;               // #674
flat out int vDraw;
#include "cx_bone_v.glsl"
#ifdef CX_CAST_LAYER
#include "cx_viewtbl.glsl"
#endif
// CX-303: what a content vertex half reads of this stage's own work: the projection the pass draws through (the
// caster's layered twin takes it per view) and the world tangent before the UV rotation turns it.
mat4 cxVP;
vec3 cxTanW;
#include "cx_host_v.glsl"
void main()
{
    // CX-303: a content pair's caster twins index the record as the caster stages do -- by baseInstance, the layered
    // one with the view packed above it -- and draw through the view's own projection
#if defined(CX_CAST_LAYER)
    int vi = gl_BaseInstance >> 18;
    int id = gl_BaseInstance & 0x3ffff;
    gl_Layer = int(uViewTbl[vi].c.w);
    mat4 uVP = uViewTbl[vi].vp;
#elif defined(CX_CAST)
    int id = gl_BaseInstance;
#else
    int id = CxSlot(uDrawBase + gl_DrawID);
#endif
    cxVP = uVP;
    CxDraw d = uDraw[id];
    vec4 pos = vec4(aPos, 1.0);
    // #137: a posed skinmesh's blended matrix carries the position and then the normal, so a cloak is lit off the
    // surface it actually has rather than the one it had in bind pose.
    int bb = CxBoneBase(d);
    mat4 skin = bb >= 0 ? CxSkinMat(bb) : mat4(1.0);
    if (bb >= 0) pos = skin * vec4(aPos, 1.0);
    vec4 world = d.model * pos;
    // Both arms are written out in full rather than folded into one line, because #119's prepass re-tests the rigid
    // expression and `invariant` only holds over an identical SEQUENCE of operations -- not merely identical
    // values. The skinned arm is the fullbright and depth stages' own, character for character.
    if (bb >= 0) gl_Position = uVP * d.model * pos;
    else gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vDepth = gl_Position.w;
    vWorld = world.xyz;
    vNrm = mat3(d.model) * (bb >= 0 ? mat3(skin) * aNrm : aNrm);
    // The tangent rides the same matrices as the normal -- a posed cloak's frame has to follow its pose --
    // and the handedness is a scalar no transform touches. d.rot rotates the UVs below, and the supplied
    // frame was derived in the UNROTATED ones, so it turns with them: T' = cos*T - sin*B over the frame's
    // own bitangent. A zero word stays zero through all of it, which is the "no frame" signal.
    vec3 aT = bb >= 0 ? mat3(skin) * aTangent.xyz : aTangent.xyz;
    vec3 aB = cross(bb >= 0 ? mat3(skin) * aNrm : aNrm, aT) * aTangent.w;
    cxTanW = mat3(d.model) * aT;
    vTan = vec4(mat3(d.model) * (d.rot.x * aT - d.rot.y * aB), aTangent.w);
    vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
    vChart = aChart;
    vDraw = id;
    CxHostVertex(d, id, bb);
}

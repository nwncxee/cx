#version 460 core
#extension GL_ARB_shader_viewport_layer_array : require
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_bone_v.glsl"

#include "cx_viewtbl.glsl"
invariant gl_Position;
out vec2 vUV;
flat out float vAlpha;
void main()
{
    int vi = gl_BaseInstance >> 18;
    CxDraw d = uDraw[gl_BaseInstance & 0x3ffff];
    gl_Layer = int(uViewTbl[vi].c.w);
    mat4 uVP = uViewTbl[vi].vp;
    // CX-227: the layered twin's skinning arm, the solid stage's, for the same posed-coverage reason
    int bb = CxBoneBase(d);
    if (bb >= 0) gl_Position = uVP * d.model * (CxSkinMat(bb) * vec4(aPos, 1.0));
    else gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vUV = aUV;
    vAlpha = d.rot.w;
}

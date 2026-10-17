#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
uniform mat4 uVP;
uniform int uDrawBase;
invariant gl_Position;
out vec2 vUV;
flat out float vAlpha;         // #126: the per-draw alpha multiplier, forwarded rather than re-read
out vec3 vWorld;               // the sky band's ray
#include "cx_bone_v.glsl"
void main()
{
    CxDraw d = uDraw[CxSlot(uDrawBase + gl_DrawID)];
    vec4 pos = vec4(aPos, 1.0);
    int bb = CxBoneBase(d);
    if (bb >= 0) pos = CxSkinMat(bb) * vec4(aPos, 1.0);
    // The skinned arm and the rigid one are the depth stage's own, character for character (#262), so the
    // prepass re-tests the same sequence of operations under `invariant`.
    if (bb >= 0) gl_Position = uVP * d.model * pos;
    else gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
    vAlpha = d.rot.w;
    vWorld = (d.model * pos).xyz;
}

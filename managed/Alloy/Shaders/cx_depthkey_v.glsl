#version 460 core
layout(location = 0) in vec3 aPos;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_bone_v.glsl"
uniform mat4 uVP;
uniform int uDrawBase;
invariant gl_Position;
out vec3 vWorld;
out float vDepth;
flat out int vDraw;
void main()
{
    int id = CxSlot(uDrawBase + gl_DrawID);
    CxDraw d = uDraw[id];
    vec4 pos = vec4(aPos, 1.0);
    int bb = CxBoneBase(d);   // #262: a dissolving skinmesh takes the same arm DepthVs does
    if (bb >= 0) pos = CxSkinMat(bb) * vec4(aPos, 1.0);
    if (bb >= 0) gl_Position = uVP * d.model * pos;
    else gl_Position = uVP * d.model * vec4(aPos, 1.0);
    // vWorld feeds the APERTURE, and it has to be the position the colour stage discards on or the
    // two passes punch different holes. That was a ponytail note while only rigid parts reached this
    // stage -- a sub-pixel speck at the threshold. #262 let skinmeshes in and a cloak's vertices sit
    // METRES from bind pose, so it stops being a speck: the prepass would keep a fragment the colour
    // pass discards, write cloak depth over it, and LEQUAL would cull the room behind the character.
    // Hence `pos`, which is the skinned position when there is one and aPos when there is not.
    // A content vertex half that displaces never reaches this stage: its pair has a depth program of its own.
    vWorld = (d.model * pos).xyz;
    vDepth = gl_Position.w;
    vDraw = id;
}

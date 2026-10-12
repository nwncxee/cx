#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_bone_v.glsl"
uniform mat4 uVP;
invariant gl_Position;
out vec2 vUV;
flat out float vAlpha;
void main()
{
    CxDraw d = uDraw[gl_BaseInstance];
    // CX-227: the solid stage's skinning arm, so a POSED coverage part can be alpha-tested -- without it
    // its holes would be punched out of a bind pose, which is why only coverage parts are admitted here.
    int bb = CxBoneBase(d);
    if (bb >= 0) gl_Position = uVP * d.model * (CxSkinMat(bb) * vec4(aPos, 1.0));
    else gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vUV = aUV;
    vAlpha = d.rot.w;
}

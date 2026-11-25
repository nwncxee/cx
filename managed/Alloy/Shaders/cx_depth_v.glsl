#version 460 core
layout(location = 0) in vec3 aPos;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_leaf.glsl"
#include "cx_bone_v.glsl"
uniform mat4 uVP;
uniform int uDrawBase;
invariant gl_Position;
flat out int vDraw;
void main()
{
    int id = CxSlot(uDrawBase + gl_DrawID);
    CxDraw d = uDraw[id];
    vDraw = id;   // #493: the record slot the vis-ID target packs
    // #262: the skinned arm, and it goes through the same `pos` TEMP the colour stages use so the
    // assigning statement is character for character theirs -- ShaderContract asserts that one
    // literal against all four stages. #119's `invariant gl_Position` is the only reason this stage
    // exists to be careful about, and everywhere else in this renderer that promise is kept by
    // identical TEXT rather than by an equivalent expression tree.
    vec4 pos = vec4(aPos, 1.0);
    int bb = CxBoneBase(d);
    if (bb >= 0) pos = CxSkinMat(bb) * vec4(aPos, 1.0);
    if (bb >= 0) gl_Position = uVP * d.model * pos;
    else gl_Position = uVP * d.model * vec4(aPos + CxLeafSway(d, aPos), 1.0);
}

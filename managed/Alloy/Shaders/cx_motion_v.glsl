#version 460 core
layout(location = 0) in vec3 aPos;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_leaf.glsl"
#include "cx_bone_v.glsl"
layout(std430, binding = 8) readonly buffer CxBonePrevBuf { mat4 uBonePrev[]; };
layout(std430, binding = 9) readonly buffer CxDanglyPrevBuf { float uDanglyPrev[]; };
uniform mat4 uVP;
uniform mat4 uPrevVP;    // last frame's view-projection, unjittered
uniform int uDrawBase;
uniform ivec2 uDangly;   // CX-26: a dangly part's snapshot -- x its first float, y its vertex count (0 = none)
invariant gl_Position;
out vec4 vCur;
out vec4 vPrev;

// CxSkinMat over last frame's palette (CX-26).
mat4 CxPrevSkinMat(int bb)
{
    mat4 m = mat4(0.0);
    if (aBoneWeight.x > 0.0) m  = uBonePrev[bb + int(aBoneIdx.x)] * aBoneWeight.x;
    if (aBoneWeight.y > 0.0) m += uBonePrev[bb + int(aBoneIdx.y)] * aBoneWeight.y;
    if (aBoneWeight.z > 0.0) m += uBonePrev[bb + int(aBoneIdx.z)] * aBoneWeight.z;
    if (aBoneWeight.w > 0.0) m += uBonePrev[bb + int(aBoneIdx.w)] * aBoneWeight.w;
    return m;
}

void main()
{
    CxDraw d = uDraw[CxSlot(uDrawBase + gl_DrawID)];
    // The depth prepass's skinned arm, text for text, so LEQUAL holds on a posed skinmesh too.
    vec4 pos = vec4(aPos, 1.0);
    int bb = CxBoneBase(d);
    if (bb >= 0) pos = CxSkinMat(bb) * vec4(aPos, 1.0);
    if (bb >= 0) gl_Position = uVP * d.model * pos;
    else gl_Position = uVP * d.model * vec4(aPos + CxLeafSway(d, aPos), 1.0);
    vCur = gl_Position;
    if (bb >= 0) vPrev = uPrevVP * d.prev * (CxPrevSkinMat(bb) * vec4(aPos, 1.0));
    else
    {
        // Item vertex k is attribute index gl_BaseVertex + k; past the snapshot it did not move.
        int k = gl_VertexID - gl_BaseVertex;
        vec3 p = aPos;
        if (k >= 0 && k < uDangly.y)
        {
            int o = uDangly.x + k * 3;
            p = vec3(uDanglyPrev[o], uDanglyPrev[o + 1], uDanglyPrev[o + 2]);
        }
        vPrev = uPrevVP * d.prev * vec4(p + CxLeafSway(d, p), 1.0);   // the same sway: only the part's own motion shows
    }
}

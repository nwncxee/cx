layout(location = 8) in vec4 aBoneIdx;
layout(location = 9) in vec4 aBoneWeight;
layout(std430, binding = 2) readonly buffer CxBoneBuf { mat4 uBone[]; };

// -1 for every draw that is not a posed skinmesh, which is nearly all of them.
int CxBoneBase(CxDraw d) { return CxSkinned(d.feat) ? int(d.feat.y) : -1; }

mat4 CxSkinMat(int bb)
{
    mat4 m = mat4(0.0);
    if (aBoneWeight.x > 0.0) m  = uBone[bb + int(aBoneIdx.x)] * aBoneWeight.x;
    if (aBoneWeight.y > 0.0) m += uBone[bb + int(aBoneIdx.y)] * aBoneWeight.y;
    if (aBoneWeight.z > 0.0) m += uBone[bb + int(aBoneIdx.z)] * aBoneWeight.z;
    if (aBoneWeight.w > 0.0) m += uBone[bb + int(aBoneIdx.w)] * aBoneWeight.w;
    return m;
}

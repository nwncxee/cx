// CX-95: a GUI scene draw the engine built and Alloy issues. The record (gl_BaseInstance) is the state the engine's
// own program would have read off GLRender's caches at the funnel: the matrix stacks, the texture 2x2, globalColor,
// the material, flags. A skinned draw carries the palette RenderSkinned had just solved.
#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec3 aNrm;
layout(location = 3) in vec4 aColor;
layout(location = 8) in vec4 aBoneIdx;
layout(location = 9) in vec4 aBoneWeight;
layout(std430, binding = 8) readonly buffer CxGuiBuf { vec4 uGui[]; };
uniform int uBoneStart;   // vec4 index of the first palette, after the records
out vec2 vUv;
out vec4 vColor;
out vec3 vNrm;
out vec3 vPos;
flat out int vRec;

mat4 GuiBone(int at) { return mat4(uGui[at], uGui[at + 1], uGui[at + 2], uGui[at + 3]); }

void main()
{
    int r = gl_BaseInstance * 16;
    mat4 mvp = mat4(uGui[r], uGui[r + 1], uGui[r + 2], uGui[r + 3]);
    mat4 mv = mat4(uGui[r + 4], uGui[r + 5], uGui[r + 6], uGui[r + 7]);
    vec4 tm = uGui[r + 8];
    vec4 misc = uGui[r + 13];
    vec4 p = vec4(aPos, 1.0);
    vec3 n = aNrm;
    if ((int(misc.x) & 16) != 0)
    {
        int bb = uBoneStart + int(misc.y);
        mat4 m = mat4(0.0);
        if (aBoneWeight.x > 0.0) m  = GuiBone(bb + int(aBoneIdx.x) * 4) * aBoneWeight.x;
        if (aBoneWeight.y > 0.0) m += GuiBone(bb + int(aBoneIdx.y) * 4) * aBoneWeight.y;
        if (aBoneWeight.z > 0.0) m += GuiBone(bb + int(aBoneIdx.z) * 4) * aBoneWeight.z;
        if (aBoneWeight.w > 0.0) m += GuiBone(bb + int(aBoneIdx.w) * 4) * aBoneWeight.w;
        p = m * p;
        n = mat3(m) * n;
    }
    vUv = vec2(dot(tm.xy, aUv), dot(tm.zw, aUv));   // inc_common: mat2(m_texture) * vTcIn
    vColor = aColor;
    vNrm = mat3(mv) * n;
    vPos = (mv * p).xyz;
    vRec = gl_BaseInstance;
    gl_Position = mvp * p;
}

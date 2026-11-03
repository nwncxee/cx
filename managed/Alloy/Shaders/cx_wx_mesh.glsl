#version 460 core
#extension GL_EXT_mesh_shader : require
// The precipitation's mesh stage: one lane per drop its task workgroup kept, each a camera-facing quad of four corners
// and two triangles, built as the vertex stage builds it (CxWxQuad).
layout(local_size_x = 32) in;
layout(triangles, max_vertices = 128, max_primitives = 64) out;
#include "cx_wx_uni.glsl"
// uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
#include "cx_wx_quad.glsl"
#include "cx_wx_load.glsl"
out vec2 vUV[];
out float vViewZ[];
out float vSeed[];
out vec3 vLight[];
out vec3 vBehind[];
out vec3 vSkyLow[];
out vec3 vLampDir[];
out float vSunVis[];
out vec3 vSide[];
out vec3 vUp[];
void main()
{
    uint n = cxWxLoad.n;
    SetMeshOutputsEXT(n * 4u, n * 2u);
    uint i = gl_LocalInvocationIndex;
    if (i >= n) return;
    uint at = i * 5u;
    for (uint c = 0u; c < 4u; c++)
    {
        vec2 corner = vec2(float((c & 1u) * 2u) - 1.0, float((c >> 1u) * 2u) - 1.0);
        CxWxCorner q = CxWxQuad(cxWxLoad.rec[at], cxWxLoad.rec[at + 1u], cxWxLoad.rec[at + 2u], cxWxLoad.rec[at + 3u],
                                cxWxLoad.rec[at + 4u], corner);
        uint v = i * 4u + c;
        vec4 vp = uView * vec4(q.pos, 1.0);
        gl_MeshVerticesEXT[v].gl_Position = uProj * vp;
        vViewZ[v] = vp.z;
        vUV[v] = q.uv;
        vSeed[v] = q.seed;
        vLight[v] = q.light;
        vBehind[v] = q.behind;
        vSkyLow[v] = q.skyLow;
        vLampDir[v] = q.lampDir;
        vSunVis[v] = q.sunVis;
        vSide[v] = q.side;
        vUp[v] = q.up;
    }
    // the quad's corners in strip order: two triangles sharing the 1-2 edge
    gl_PrimitiveTriangleIndicesEXT[i * 2u] = uvec3(i * 4u, i * 4u + 1u, i * 4u + 2u);
    gl_PrimitiveTriangleIndicesEXT[i * 2u + 1u] = uvec3(i * 4u + 2u, i * 4u + 1u, i * 4u + 3u);
}

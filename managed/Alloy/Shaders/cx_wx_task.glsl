#version 460 core
#extension GL_EXT_mesh_shader : require
// The precipitation's task stage: one lane per lattice point, the same decision the compute cull makes (CxWxPlace),
// the kept drops packed for one mesh workgroup, and none launched where nothing was kept.
layout(local_size_x = 32) in;
#include "cx_wx_place.glsl"
#include "cx_wx_load.glsl"
shared uint cxWxKept;
void main()
{
    if (gl_LocalInvocationIndex == 0u) cxWxKept = 0u;
    barrier();
    vec4 r0, r1, r2, r3, r4;
    if (CxWxPlace(gl_GlobalInvocationID.x, r0, r1, r2, r3, r4))
    {
        uint at = atomicAdd(cxWxKept, 1u) * 5u;
        cxWxLoad.rec[at] = r0;
        cxWxLoad.rec[at + 1u] = r1;
        cxWxLoad.rec[at + 2u] = r2;
        cxWxLoad.rec[at + 3u] = r3;
        cxWxLoad.rec[at + 4u] = r4;
    }
    barrier();
    uint n = cxWxKept;
    if (gl_LocalInvocationIndex == 0u) cxWxLoad.n = n;
    EmitMeshTasksEXT(n > 0u ? 1u : 0u, 1u, 1u);
}

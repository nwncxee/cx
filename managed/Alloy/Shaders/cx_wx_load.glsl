// What a task workgroup hands its mesh workgroup: the drops it kept, five records each (CxWxPlace), packed.
#ifndef CX_WX_LOAD
#define CX_WX_LOAD
/// Lattice points one task workgroup walks, and so the most drops one mesh workgroup emits.
const uint CX_WX_GROUP = 32u;
struct CxWxLoad
{
    vec4 rec[CX_WX_GROUP * 5u];
    uint n;
};
taskPayloadSharedEXT CxWxLoad cxWxLoad;
#endif

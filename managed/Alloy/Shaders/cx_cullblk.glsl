// CX-316: the cull dispatch's terms -- one std140 block both compute stages read, filled once per dispatch
// by CullPass.CullBlock and bound before it. Members are in the block's std140 order, which is also the
// staging struct's field order; CullPass.SelfCheck pins the two together.
#ifndef CX_CULL_BLOCK
#define CX_CULL_BLOCK
layout(std140, binding = 3) uniform CxCull
{
    mat4 uVP;
    // #489: the pyramid's OWN view-projection -- this pass's for the same-frame phases, the prior
    // pass's for phase one, which tests the sphere in the clip space the pyramid was built in.
    mat4 uPyrVP;
    vec4 uPlanes[6];
    vec4 uFogOrigin;  // xyz = the player's feet, w = 1 radial from it, 0 = view depth (as the fog)
    vec4 uCullEye;    // xyz = this pass's eye; w unused
    vec2 uPyrSize;
    float uMaxLod;
    float uLodPx;
    float uLodScale;
    float uFogCull;   // #341: fog-opaque distance, or 0 for no fog cull
    float uClipZ;     // CX-51: the mirror plane; a sphere wholly below it is not drawn (-1e30 = none)
    int uDrawN;
    int uHiZ;
    // #489: 0 = colour phase; 1 = prepass phase one (WRITE the hi-Z verdict per draw);
    // 2 = prepass phase two (only a marked draw is tested, against the fresh pyramid).
    int uRej;
    int uCompactBelow;
};
#endif

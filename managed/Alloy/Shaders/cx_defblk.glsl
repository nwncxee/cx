// CX-316: the deferred lighting dispatch's own terms -- one std140 block every variant of this stage reads
// (the walk, the in-loop-tap escape and the film pass), filled once per dispatch by DeferredBlock. The
// shared lighting libraries' terms are not here: those belong to whatever program includes them.
#ifndef CX_DEF_BLOCK
#define CX_DEF_BLOCK
layout(std140, binding = 3) uniform CxDeferred
{
    // The JITTERED view-projection the prepass rasterised with, inverted: world positions come back from it.
    mat4 uInvVp;
    // ...and that projection itself: the depth row ([2].z, [3].z) linearises the buffer and the diagonal
    // ([0].x, [1].y) sizes a tile in view space. Read off the matrix this pass was handed rather than from
    // separately uploaded planes, which can drift from the frustum the depth was written with (cx_scenedepth
    // takes the same care).
    mat4 uProj;
    mat4 uView;      // world -> view, for the tile cull's light positions; rigid, so its inverse is its transpose
    vec3 uEyePos;
    int uTiles;      // light.deferredtiles: 1 = this group's own culled list, 0 = each pixel's froxel list
    ivec4 uRect;     // the render viewport: xy = origin in window pixels, zw = size
    ivec2 uShSize;   // the visibility record's size in texels
    int uLightCount;
    int uShRes;      // shadow.deferredres: the record's divisor (1 full, 2 half); 0 = no record this frame
};
#endif

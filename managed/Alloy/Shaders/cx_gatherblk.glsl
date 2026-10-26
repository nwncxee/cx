// CX-344: the caster cull's terms -- one std140 block both gather dispatches read, filled once per dispatch
// by CasterGather.GatherBlock and bound before it. Members are in the block's std140 order, which is also the
// staging struct's field order; UboSlots.Check pins the two together. uViewN belongs to the LAYERED dispatch
// alone and the per-view one leaves it unread; uFocus, uDir, uReach, uAlong, uPoint, uSet and the two detail
// thresholds are the per-view dispatch's, which the layered one reads out of the view table instead.
#ifndef CX_GATHER_BLOCK
#define CX_GATHER_BLOCK
layout(std140, binding = 3) uniform CxGather
{
    vec4 uPlanes[6];
    vec3 uFocus;
    float uReach;
    vec3 uDir;
    float uAlong;
    float uLodR1;
    float uLodR2;
    int uDrawN;
    int uViewN;
    int uPoint;
    int uCull;
    int uSet;
    int uRx;
    int uGroups;
};
#endif

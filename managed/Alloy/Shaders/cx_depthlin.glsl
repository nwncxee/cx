// The one decode from world depth to view distance: every shader that turns the scene's depth into metres calls it,
// so the decode the reversed-depth golden holds is every reader's (tools/gates/world-depth.sh refuses another copy).
#ifndef CX_DEPTH_LIN
#define CX_DEPTH_LIN
// Positive view distance of world depth `d` under the folded world projection whose depth row is `k`, its
// (M33, M43) as (proj[2].z, proj[3].z): z_clip = z_v*M33 + M43, w_clip = -z_v, so z_v = -M43 / (ndc + M33). The
// world's depth is reversed (near 1, far 0) and its projection folded to match, so the stored depth IS the NDC z.
float CxDepthViewZ(float d, vec2 k)
{
    return k.y / (d + k.x);
}

// The same, under the whole folded world projection `proj`.
float CxDepthViewZ(float d, mat4 proj)
{
    return CxDepthViewZ(d, vec2(proj[2].z, proj[3].z));
}
#endif
